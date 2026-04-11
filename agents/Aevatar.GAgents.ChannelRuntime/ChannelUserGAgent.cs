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
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RecoveryTimeoutDelay = TimeSpan.FromMilliseconds(1);

    // Non-persisted accumulator for streaming text deltas.
    // Actor is single-threaded — plain Dictionary is correct (no locks).
    private readonly Dictionary<string, StringBuilder> _responseBuilders = new();

    // Timeout leases keyed by sessionId — needed to cancel on completion.
    private readonly Dictionary<string, RuntimeCallbackLease> _timeoutLeases = new();

    // In-memory dedup: prevents duplicate HandleInbound for the same Lark messageId.
    // Covers Lark webhook retries and any stream-level redelivery within the same
    // grain activation. Bounded to prevent unbounded growth.
    private const int MaxProcessedMessageIds = 200;
    private readonly LinkedList<string> _processedMessageIdsOrder = new();
    private readonly HashSet<string> _processedMessageIds = new(StringComparer.Ordinal);

    protected override ChannelUserState TransitionState(ChannelUserState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChannelUserTrackedEvent>(ApplyTracked)
            .On<ChannelUserBoundEvent>(ApplyBound)
            .On<ChannelChatRequestedEvent>(ApplyRequested)
            .On<ChannelChatCompletedEvent>(ApplyCompleted)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (State.PendingSessions.Count == 0)
            return;

        foreach (var session in State.PendingSessions.Values)
        {
            try
            {
                await RecoverPendingSessionAsync(session, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Failed to recover pending chat session {SessionId} for actor {ActorId}",
                    session.SessionId,
                    Id);
                RecordDiagnostic("Chat:recover:error", session.Platform, session.RegistrationId,
                    $"{session.SessionId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ─── Turn 1: Inbound message → dispatch chat request ───

    [EventHandler]
    public async Task HandleInbound(ChannelInboundEvent evt)
    {
        try
        {
            // 0. Dedup: skip if we've already successfully dispatched this Lark messageId.
            if (!string.IsNullOrEmpty(evt.MessageId) && _processedMessageIds.Contains(evt.MessageId))
            {
                RecordDiagnostic("Chat:dedup", evt.Platform, evt.RegistrationId,
                    $"duplicate messageId={evt.MessageId}");
                return;
            }

            // A pending session without a processed messageId marker means a prior turn
            // persisted the session but did not finish dispatching ChatRequestEvent.
            // Re-enter recovery instead of dropping the retry on the floor.
            var existingPendingSession = FindPendingSessionByMessageId(evt.MessageId);
            if (existingPendingSession != null)
            {
                RecordDiagnostic("Chat:resume-pending", evt.Platform, evt.RegistrationId,
                    $"pending messageId={evt.MessageId} sessionId={existingPendingSession.SessionId}");
                await RecoverPendingSessionAsync(existingPendingSession, CancellationToken.None);
                return;
            }

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
            await streams.GetStream(chatActorId).UpsertRelayAsync(BuildChatRelayBinding(chatActorId));

            // 5. Schedule timeout BEFORE persisting the session. If this fails,
            // nothing has been persisted — no session to strand, no cleanup needed.
            // The exception propagates, the turn fails, and Orleans stream redelivery
            // can retry the whole flow (endpoint-level dedup doesn't apply to stream
            // retries). HandleChatTimeout tolerates a missing session (no-op).
            //
            // SessionId is deterministic when messageId is available: retries of the
            // same inbound event produce the same sessionId → same timeout key, so the
            // scheduler upserts instead of accumulating orphan durable timeouts.
            var sessionId = !string.IsNullOrEmpty(evt.MessageId)
                ? DeriveSessionId(evt.MessageId)
                : Guid.NewGuid().ToString("N");
            var timeoutAt = DateTimeOffset.UtcNow.Add(ChatTimeout);
            var lease = await ScheduleSelfDurableTimeoutAsync(
                $"chat-timeout-{sessionId}",
                ChatTimeout,
                new ChannelChatTimeoutEvent { SessionId = sessionId });
            _timeoutLeases[sessionId] = lease;

            var pendingSession = new ChannelPendingChatSession
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
                Prompt = evt.Text,
                TimeoutAt = Timestamp.FromDateTimeOffset(timeoutAt),
            };

            // 6. Persist chat request with full replay context (durable — survives restart).
            // State.PendingSessions is populated by ApplyRequested via event sourcing.
            await PersistDomainEventAsync(new ChannelChatRequestedEvent
            {
                Session = pendingSession,
            });

            RecordDiagnostic("Chat:start", evt.Platform, evt.RegistrationId, $"sessionId={sessionId}");

            // 7. Build and dispatch ChatRequestEvent to chat actor
            await DispatchChatRequestAsync(pendingSession, effectiveToken, chatActor, CancellationToken.None);

            // 8. Record successful dispatch in dedup set.
            TrackProcessedMessageId(evt.MessageId);

            // RETURN — end turn. Continuation happens in HandleChatContent/HandleChatEnd.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "HandleInbound failed: platform={Platform}, registrationId={RegistrationId}, messageId={MessageId}",
                evt.Platform,
                evt.RegistrationId,
                evt.MessageId);
            RecordDiagnostic("Chat:error", evt.Platform, evt.RegistrationId,
                $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    // ─── Turn 2+: Accumulate streaming text deltas ───

    [EventHandler]
    public Task HandleChatContent(TextMessageContentEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId) || !State.PendingSessions.ContainsKey(evt.SessionId))
            return Task.CompletedTask;

        if (!_responseBuilders.TryGetValue(evt.SessionId, out var builder))
        {
            builder = new StringBuilder();
            _responseBuilders[evt.SessionId] = builder;
        }
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

        var replyText = _responseBuilders.Remove(evt.SessionId, out var builder)
            ? builder.ToString()
            : evt.Content;

        RecordDiagnostic("Chat:done", session.Platform, session.RegistrationId,
            $"reply_length={replyText?.Length}");

        await SendReplyAndCompleteAsync(session, replyText);
    }

    // ─── Turn T: Timeout → send partial/timeout reply ───

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleChatTimeout(ChannelChatTimeoutEvent evt)
    {
        if (!State.PendingSessions.TryGetValue(evt.SessionId, out var session))
            return; // Already completed

        var partial = _responseBuilders.Remove(evt.SessionId, out var builder)
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
        if (_timeoutLeases.Remove(session.SessionId, out var lease))
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

    private async Task RecoverPendingSessionAsync(
        ChannelPendingChatSession session,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || string.IsNullOrWhiteSpace(session.ChatActorId))
            return;

        var timeoutDelay = ComputeRecoveryTimeoutDelay(session, DateTimeOffset.UtcNow, out var shouldRedispatch);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            $"chat-timeout-{session.SessionId}",
            timeoutDelay,
            new ChannelChatTimeoutEvent { SessionId = session.SessionId },
            ct: ct);
        _timeoutLeases[session.SessionId] = lease;

        if (!shouldRedispatch)
        {
            RecordDiagnostic("Chat:recover:timeout", session.Platform, session.RegistrationId,
                $"sessionId={session.SessionId}");
            return;
        }

        var actorRuntime = Services.GetRequiredService<IActorRuntime>();
        var streams = Services.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        var chatActor = await actorRuntime.GetAsync(session.ChatActorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(session.ChatActorId, ct);

        await streams.GetStream(session.ChatActorId).UpsertRelayAsync(
            BuildChatRelayBinding(session.ChatActorId),
            ct);

        var effectiveToken = !string.IsNullOrEmpty(State.NyxidAccessToken)
            ? State.NyxidAccessToken
            : session.OrgToken;
        await DispatchChatRequestAsync(session, effectiveToken, chatActor, ct);
        TrackProcessedMessageId(session.MessageId);

        RecordDiagnostic("Chat:recover:redispatch", session.Platform, session.RegistrationId,
            $"sessionId={session.SessionId}");
    }

    private async Task DispatchChatRequestAsync(
        ChannelPendingChatSession session,
        string effectiveToken,
        IActor chatActor,
        CancellationToken ct)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = session.Prompt,
            SessionId = session.SessionId,
            ScopeId = session.RegistrationScopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken;
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdOrgToken] = session.OrgToken;
        chatRequest.Metadata["scope_id"] = session.RegistrationScopeId;
        chatRequest.Metadata[ChannelMetadataKeys.Platform] = session.Platform;
        chatRequest.Metadata[ChannelMetadataKeys.SenderId] = session.SenderId;
        chatRequest.Metadata[ChannelMetadataKeys.SenderName] = session.SenderName;
        chatRequest.Metadata[ChannelMetadataKeys.MessageId] = session.MessageId;
        chatRequest.Metadata[ChannelMetadataKeys.ChatType] = session.ChatType;

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
        await chatActor.HandleEventAsync(chatEnvelope, ct);
    }

    // ─── Helpers ───

    private StreamForwardingBinding BuildChatRelayBinding(string chatActorId) =>
        new()
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
        };

    private static TimeSpan ComputeRecoveryTimeoutDelay(
        ChannelPendingChatSession session,
        DateTimeOffset now,
        out bool shouldRedispatch)
    {
        if (string.IsNullOrWhiteSpace(session.Prompt))
        {
            shouldRedispatch = false;
            return RecoveryTimeoutDelay;
        }

        if (session.TimeoutAt == null)
        {
            shouldRedispatch = true;
            return ChatTimeout;
        }

        var remaining = session.TimeoutAt.ToDateTimeOffset() - now;
        if (remaining > TimeSpan.Zero)
        {
            shouldRedispatch = true;
            return remaining;
        }

        shouldRedispatch = false;
        return RecoveryTimeoutDelay;
    }

    private ChannelPendingChatSession? FindPendingSessionByMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        return State.PendingSessions.Values.FirstOrDefault(session =>
            string.Equals(session.MessageId, messageId, StringComparison.Ordinal));
    }

    private void TrackProcessedMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId) || !_processedMessageIds.Add(messageId))
            return;

        _processedMessageIdsOrder.AddLast(messageId);
        while (_processedMessageIdsOrder.Count > MaxProcessedMessageIds)
        {
            var oldest = _processedMessageIdsOrder.First!.Value;
            _processedMessageIdsOrder.RemoveFirst();
            _processedMessageIds.Remove(oldest);
        }
    }

    /// <summary>
    /// Derives a deterministic GUID-format sessionId from a messageId.
    /// Same messageId always produces the same sessionId, so Orleans stream
    /// retries reuse the same timeout key (scheduler upserts, no orphans).
    /// Output is 32 hex chars — same format as Guid.NewGuid().ToString("N").
    /// </summary>
    private static string DeriveSessionId(string messageId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(messageId));
        return new Guid(hash[..16]).ToString("N");
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
        if (evt.Session == null || string.IsNullOrEmpty(evt.Session.SessionId))
            return current;

        var next = current.Clone();
        next.PendingSessions[evt.Session.SessionId] = evt.Session;
        return next;
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
