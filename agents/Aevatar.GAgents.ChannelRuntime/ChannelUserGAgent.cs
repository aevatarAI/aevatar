using System.Collections.Concurrent;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Per-sender coordinator actor. Owns identity state and orchestrates the
/// full inbound message flow using the continuation pattern:
///
///   HandleInbound (turn 1): persist request → set up stream forwarding →
///     dispatch ChatRequestEvent → schedule timeout → RETURN
///   HandleChatContent (turn 2+): accumulate response deltas
///   HandleChatEnd (turn N): send reply → persist completion → cleanup
///   HandleChatTimeout (turn T): send timeout reply → persist completion → cleanup
///
/// No turn awaits another actor's response — each handler is a separate
/// grain turn, avoiding Orleans grain scheduler deadlock.
///
/// Actor ID convention: channel-user-{platform}-{registrationId}-{senderId}
/// </summary>
public sealed class ChannelUserGAgent : GAgentBase<ChannelUserState>
{
    // Non-persisted accumulator for streaming text deltas. Rebuilt on replay
    // (HandleChatContent events are replayed from event store on activation).
    private readonly ConcurrentDictionary<string, StringBuilder> _responseBuilders = new();

    // Timeout leases keyed by sessionId — needed to cancel on completion.
    private readonly ConcurrentDictionary<string, RuntimeCallbackLease> _timeoutLeases = new();

    protected override ChannelUserState TransitionState(ChannelUserState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelUserTrackedEvent>(ApplyTracked)
            .On<ChannelUserBoundEvent>(ApplyBound)
            .On<ChannelChatRequestedEvent>(ApplyRequested)
            .On<ChannelChatCompletedEvent>(ApplyCompleted)
            .OrCurrent();

    // ─── Turn 1: Inbound message → dispatch chat request ───

    [EventHandler]
    public async Task HandleInbound(ChannelInboundEvent evt)
    {
        // 1. Track sender identity
        await PersistDomainEventAsync(new ChannelUserTrackedEvent
        {
            Platform = evt.Platform,
            PlatformUserId = evt.SenderId,
            DisplayName = evt.SenderName,
        });

        // 2. Resolve tokens
        var orgToken = evt.RegistrationToken;
        var effectiveToken = !string.IsNullOrEmpty(State.NyxidAccessToken)
            ? State.NyxidAccessToken
            : orgToken;

        // 3. Create/get chat actor
        var chatActorId = $"channel-{evt.Platform}-{evt.RegistrationId}-{evt.SenderId}";
        var actorRuntime = Services.GetRequiredService<IActorRuntime>();
        var chatActor = await actorRuntime.GetAsync(chatActorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(chatActorId);

        // 4. Set up stream forwarding: chat actor → self
        // Events published with TopologyAudience.Parent (no parent → own stream)
        // are forwarded to this actor's stream, arriving as separate grain turns.
        var streams = Services.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        await streams.GetStream(chatActorId).UpsertRelayAsync(new StreamForwardingBinding
        {
            SourceStreamId = chatActorId,
            TargetStreamId = Id,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = new HashSet<TopologyAudience> { TopologyAudience.Parent },
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                Google.Protobuf.WellKnownTypes.Any.Pack(new TextMessageContentEvent()).TypeUrl,
                Google.Protobuf.WellKnownTypes.Any.Pack(new TextMessageEndEvent()).TypeUrl,
            },
        });

        // 5. Persist chat request (durable — survives restart)
        var sessionId = Guid.NewGuid().ToString("N");
        await PersistDomainEventAsync(new ChannelChatRequestedEvent
        {
            SessionId = sessionId,
            ChatActorId = chatActorId,
        });

        // Also persist the replay context in state
        State.PendingSessions[sessionId] = new ChannelPendingChatSession
        {
            SessionId = sessionId,
            ChatActorId = chatActorId,
            OrgToken = orgToken,
            Platform = evt.Platform,
            ConversationId = evt.ConversationId,
            SenderId = evt.SenderId,
            SenderName = evt.SenderName,
            MessageId = evt.MessageId,
            ChatType = evt.ChatType,
            RegistrationId = evt.RegistrationId,
            NyxProviderSlug = evt.NyxProviderSlug,
            RegistrationScopeId = evt.RegistrationScopeId,
        };

        RecordDiagnostic("Chat:start", evt.Platform, evt.RegistrationId, $"sessionId={sessionId}");

        // 6. Build and dispatch ChatRequestEvent to chat actor
        var chatRequest = new ChatRequestEvent
        {
            Prompt = evt.Text,
            SessionId = sessionId,
            ScopeId = evt.RegistrationScopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken;
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = orgToken;
        chatRequest.Metadata["scope_id"] = evt.RegistrationScopeId;
        chatRequest.Metadata[ChannelMetadataKeys.Platform] = evt.Platform;
        chatRequest.Metadata[ChannelMetadataKeys.SenderId] = evt.SenderId;
        chatRequest.Metadata[ChannelMetadataKeys.SenderName] = evt.SenderName;
        chatRequest.Metadata[ChannelMetadataKeys.MessageId] = evt.MessageId;
        chatRequest.Metadata[ChannelMetadataKeys.ChatType] = evt.ChatType;

        var chatEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(chatRequest),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = chatActor.Id },
            },
        };
        await chatActor.HandleEventAsync(chatEnvelope);

        // 7. Schedule timeout (120s) — fires ChannelChatTimeoutEvent as self-message
        var lease = await ScheduleSelfDurableTimeoutAsync(
            $"chat-timeout-{sessionId}",
            TimeSpan.FromSeconds(120),
            new ChannelChatTimeoutEvent { SessionId = sessionId });
        _timeoutLeases[sessionId] = lease;

        // RETURN — end turn. Continuation happens in HandleChatContent/HandleChatEnd.
    }

    // ─── Turn 2+: Accumulate streaming text deltas ───

    [EventHandler]
    public Task HandleChatContent(TextMessageContentEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId) || !State.PendingSessions.ContainsKey(evt.SessionId))
            return Task.CompletedTask;

        var builder = _responseBuilders.GetOrAdd(evt.SessionId, _ => new StringBuilder());
        if (!string.IsNullOrEmpty(evt.Delta))
            builder.Append(evt.Delta);

        return Task.CompletedTask;
    }

    // ─── Turn N: Chat complete → send reply ───

    [EventHandler]
    public async Task HandleChatEnd(TextMessageEndEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId) || !State.PendingSessions.TryGetValue(evt.SessionId, out var session))
            return;

        var replyText = _responseBuilders.TryRemove(evt.SessionId, out var builder)
            ? builder.ToString()
            : evt.Content;

        RecordDiagnostic("Chat:done", session.Platform, session.RegistrationId,
            $"reply_length={replyText?.Length}");

        await SendReplyAndCompleteAsync(session, replyText);
    }

    // ─── Turn T: Timeout → send partial/timeout reply ───

    [EventHandler(OnlySelfHandling = true)]
    public async Task HandleChatTimeout(ChannelChatTimeoutEvent evt)
    {
        if (!State.PendingSessions.TryGetValue(evt.SessionId, out var session))
            return; // Already completed

        var partial = _responseBuilders.TryRemove(evt.SessionId, out var builder)
            ? builder.ToString()
            : string.Empty;

        var replyText = partial.Length > 0
            ? partial
            : "Sorry, it's taking too long to respond. Please try again.";

        RecordDiagnostic("Chat:timeout", session.Platform, session.RegistrationId,
            $"partial_length={partial.Length}");

        await SendReplyAndCompleteAsync(session, replyText);
    }

    // ─── Shared: send reply + persist completion + cleanup ───

    private async Task SendReplyAndCompleteAsync(ChannelPendingChatSession session, string? replyText)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";

        // Send reply via platform adapter
        try
        {
            var delivery = await SendPlatformReplyAsync(session, replyText);
            if (delivery.Succeeded)
            {
                RecordDiagnostic("Reply:done", session.Platform, session.RegistrationId,
                    delivery.Detail);
            }
            else
            {
                RecordDiagnostic("Reply:error", session.Platform, session.RegistrationId,
                    delivery.Detail);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendReply failed: platform={Platform}, session={SessionId}",
                session.Platform, session.SessionId);
            RecordDiagnostic("Reply:error", session.Platform, session.RegistrationId,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // Persist completion (removes from pending_sessions via state transition)
        await PersistDomainEventAsync(new ChannelChatCompletedEvent { SessionId = session.SessionId });

        // Cancel timeout if still pending
        if (_timeoutLeases.TryRemove(session.SessionId, out var lease))
        {
            try
            {
                var scheduler = Services.GetService<IActorRuntimeCallbackScheduler>();
                if (scheduler != null)
                    await scheduler.CancelAsync(lease);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to cancel timeout for session {SessionId}", session.SessionId);
            }
        }

        // Remove stream forwarding
        try
        {
            var streams = Services.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
            await streams.GetStream(session.ChatActorId).RemoveRelayAsync(Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to remove relay for session {SessionId}", session.SessionId);
        }
    }

    private async Task<PlatformReplyDeliveryResult> SendPlatformReplyAsync(
        ChannelPendingChatSession session, string replyText)
    {
        var adapters = Services.GetRequiredService<IEnumerable<IPlatformAdapter>>();
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, session.Platform, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
            return new PlatformReplyDeliveryResult(false, $"No adapter for platform: {session.Platform}");

        var nyxClient = Services.GetRequiredService<NyxIdApiClient>();
        var inbound = new InboundMessage
        {
            Platform = session.Platform,
            ConversationId = session.ConversationId,
            SenderId = session.SenderId,
            SenderName = session.SenderName,
            Text = string.Empty,
            MessageId = session.MessageId,
            ChatType = session.ChatType,
        };

        var registration = new ChannelBotRegistrationEntry
        {
            Id = session.RegistrationId,
            Platform = session.Platform,
            NyxProviderSlug = session.NyxProviderSlug,
            NyxUserToken = session.OrgToken,
            ScopeId = session.RegistrationScopeId,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await adapter.SendReplyAsync(replyText, inbound, registration, nyxClient, cts.Token);
    }

    // ─── State transitions ───

    private static ChannelUserState ApplyTracked(ChannelUserState current, ChannelUserTrackedEvent evt)
    {
        var next = current.Clone();
        var now = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (string.IsNullOrEmpty(next.Platform))
        {
            next.Platform = evt.Platform;
            next.PlatformUserId = evt.PlatformUserId;
            next.FirstSeen = now;
        }

        next.DisplayName = evt.DisplayName;
        next.LastSeen = now;
        return next;
    }

    private static ChannelUserState ApplyBound(ChannelUserState current, ChannelUserBoundEvent evt)
    {
        var next = current.Clone();
        next.NyxidUserId = evt.NyxidUserId;
        next.NyxidAccessToken = evt.NyxidAccessToken;
        return next;
    }

    private static ChannelUserState ApplyRequested(ChannelUserState current, ChannelChatRequestedEvent evt)
    {
        // Session details are set directly in HandleInbound after PersistDomainEventAsync.
        // This transition is a no-op — the state is already updated.
        return current;
    }

    private static ChannelUserState ApplyCompleted(ChannelUserState current, ChannelChatCompletedEvent evt)
    {
        var next = current.Clone();
        next.PendingSessions.Remove(evt.SessionId);
        return next;
    }

    // ─── Diagnostics ───

    private void RecordDiagnostic(string stage, string platform, string registrationId, string? detail = null)
    {
        var cache = Services.GetService<IMemoryCache>();
        if (cache == null) return;

        var entries = cache.GetOrCreate(ChannelDiagnosticKeys.RecentErrors, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new List<object>();
        })!;

        lock (entries)
        {
            entries.Add(new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                stage,
                platform,
                registrationId,
                detail,
            });
            while (entries.Count > 50)
                entries.RemoveAt(0);
        }
    }
}
