using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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

    // Progressive reply pacing. On first delta the adapter posts a placeholder
    // message; subsequent deltas PATCH it at most once per throttle interval to
    // keep platform rate limits happy while still feeling responsive. Final
    // HandleChatEnd always flushes regardless of throttle.
    private static readonly TimeSpan StreamingEditThrottle = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StreamingHttpTimeout = TimeSpan.FromSeconds(10);

    // Non-persisted accumulator for streaming text deltas.
    // Actor is single-threaded — plain Dictionary is correct (no locks).
    private readonly Dictionary<string, StringBuilder> _responseBuilders = new();

    // Per-session progressive-delivery state. Lost on restart — recovery
    // falls back to posting a fresh final reply via the non-streaming path.
    private readonly Dictionary<string, StreamingDeliveryState> _streamingDeliveries = new();

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
            .On<ChannelInboundDispatchedEvent>(ApplyInboundDispatched)
            .On<ChannelInboundHandledEvent>(ApplyInboundHandled)
            .On<ChannelChatCompletedEvent>(ApplyCompleted)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        // Restore durable dedup set from persisted state.
        foreach (var id in State.ProcessedMessageIds)
        {
            if (_processedMessageIds.Add(id))
                _processedMessageIdsOrder.AddLast(id);
        }

        if (State.PendingSessions.Count == 0)
            return;

        RecordDiagnostic("Actor:recovering", State.Platform, "unknown",
            $"actor={Id} pending_sessions={State.PendingSessions.Count}");

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
            RecordDiagnostic("Inbound:received", evt.Platform, evt.RegistrationId,
                $"actor={Id} messageId={evt.MessageId} text_length={evt.Text?.Length ?? 0}");

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

            if (await TryHandleAgentBuilderAsync(evt, effectiveToken, CancellationToken.None))
                return;

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

            RecordDiagnostic("Chat:dispatching", evt.Platform, evt.RegistrationId,
                $"sessionId={sessionId} chatActorId={chatActorId}");

            // 7. Build and dispatch ChatRequestEvent to chat actor
            await DispatchChatRequestAsync(pendingSession, effectiveToken, chatActor, CancellationToken.None);

            RecordDiagnostic("Chat:dispatched", evt.Platform, evt.RegistrationId,
                $"sessionId={sessionId}");

            // 8. Record successful dispatch in durable dedup state.
            await PersistProcessedMessageIdAsync(pendingSession, CancellationToken.None);

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
    public async Task HandleChatContent(TextMessageContentEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId) ||
            !State.PendingSessions.TryGetValue(evt.SessionId, out var session))
        {
            return;
        }

        if (!_responseBuilders.TryGetValue(evt.SessionId, out var builder))
        {
            builder = new StringBuilder();
            _responseBuilders[evt.SessionId] = builder;
            RecordDiagnostic("Chat:content:first", session.Platform, session.RegistrationId,
                $"sessionId={evt.SessionId} delta_length={evt.Delta?.Length ?? 0}");
        }
        if (!string.IsNullOrEmpty(evt.Delta))
            builder.Append(evt.Delta);

        var accumulated = builder.ToString();
        if (string.IsNullOrWhiteSpace(accumulated))
            return;

        await TryStreamingUpdateAsync(session, accumulated, isFinal: false);
    }

    // ─── Turn N: Chat complete → send reply ───

    [EventHandler]
    public async Task HandleChatEnd(TextMessageEndEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId) || !State.PendingSessions.TryGetValue(evt.SessionId, out var session))
        {
            // Record even when session not found — helps diagnose ordering issues
            RecordDiagnostic("Chat:end:orphan", "unknown", "unknown",
                $"sessionId={evt.SessionId} pending_count={State.PendingSessions.Count}");
            return;
        }

        var replyText = _responseBuilders.TryGetValue(evt.SessionId, out var builder)
            ? builder.ToString()
            : evt.Content;

        RecordDiagnostic("Chat:done", session.Platform, session.RegistrationId,
            $"reply_length={replyText?.Length}");

        await SendReplyAndCompleteAsync(session, replyText, forceComplete: false);
    }

    // ─── Turn T: Timeout → send partial/timeout reply ───

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleChatTimeout(ChannelChatTimeoutEvent evt)
    {
        if (!State.PendingSessions.TryGetValue(evt.SessionId, out var session))
        {
            RecordDiagnostic("Chat:timeout:completed", "unknown", "unknown",
                $"sessionId={evt.SessionId} (already completed)");
            return; // Already completed
        }

        var partial = _responseBuilders.TryGetValue(evt.SessionId, out var builder)
            ? builder.ToString()
            : string.Empty;

        var replyText = partial.Length > 0
            ? partial
            : "Sorry, it's taking too long to respond. Please try again.";

        RecordDiagnostic("Chat:timeout", session.Platform, session.RegistrationId,
            $"partial_length={partial.Length}");

        // Timeout is the last chance — always complete to prevent permanent stranding.
        await SendReplyAndCompleteAsync(session, replyText, forceComplete: true);
    }

    // ─── Shared: send reply + persist completion + cleanup ───

    /// <param name="forceComplete">
    /// When false (HandleChatEnd): reply failure skips completion so the timeout
    /// can retry later. When true (HandleChatTimeout): always completes to prevent
    /// permanent session stranding — no further retry exists.
    /// </param>
    private async Task SendReplyAndCompleteAsync(
        ChannelPendingChatSession session, string? replyText, bool forceComplete)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";

        RecordDiagnostic("Reply:sending", session.Platform, session.RegistrationId,
            $"sessionId={session.SessionId} reply_length={replyText.Length} forceComplete={forceComplete}");

        // If the progressive path already posted a placeholder, the user is
        // looking at that message — finalize by PATCHing it. Posting a fresh
        // message would leave a duplicate visible reply.
        var streamingActive = _streamingDeliveries.TryGetValue(session.SessionId, out var streamState)
                              && streamState is { MessageId: not null, Disabled: false };

        var replySucceeded = false;
        try
        {
            if (streamingActive)
            {
                replySucceeded = await TryStreamingUpdateAsync(session, replyText, isFinal: true);
                if (replySucceeded)
                {
                    RecordDiagnostic("Reply:done", session.Platform, session.RegistrationId,
                        $"stream message_id={streamState!.MessageId} edits={streamState.EditCount}");
                }
                else
                {
                    RecordDiagnostic("Reply:error", session.Platform, session.RegistrationId,
                        $"stream final PATCH failed messageId={streamState!.MessageId}");
                    replySucceeded = await TrySendDirectReplyAsync(
                        session,
                        replyText,
                        "stream_fallback");
                }
            }
            else
            {
                replySucceeded = await TrySendDirectReplyAsync(session, replyText);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendReply failed: platform={Platform}, session={SessionId}",
                session.Platform, session.SessionId);
            RecordDiagnostic("Reply:error", session.Platform, session.RegistrationId,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // On reply failure from HandleChatEnd, keep the session open so the
        // timeout can retry. On timeout (forceComplete), always complete.
        if (!replySucceeded && !forceComplete)
            return;

        // Persist completion (removes from pending_sessions via state transition)
        await PersistDomainEventAsync(new ChannelChatCompletedEvent { SessionId = session.SessionId });
        _responseBuilders.Remove(session.SessionId);
        _streamingDeliveries.Remove(session.SessionId);

        RecordDiagnostic("Session:completed", session.Platform, session.RegistrationId,
            $"sessionId={session.SessionId} reply_succeeded={replySucceeded}");

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
        var adapter = ResolvePlatformAdapter(session.Platform);
        if (adapter is null)
            return new PlatformReplyDeliveryResult(false, $"No adapter for platform: {session.Platform}");

        var nyxClient = Services.GetRequiredService<NyxIdApiClient>();
        var inbound = BuildInboundFromSession(session);
        var registration = BuildRegistrationFromSession(session);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await adapter.SendReplyAsync(replyText, inbound, registration, nyxClient, cts.Token);
    }

    // ─── Progressive reply: post placeholder, PATCH as deltas stream in ───

    /// <summary>
    /// If the session's platform supports streaming replies, post the placeholder
    /// (first call) or PATCH it with the accumulated text (throttled). When
    /// <paramref name="isFinal"/>, bypass the throttle — used by HandleChatEnd /
    /// HandleChatTimeout to flush the last state. Returns true iff the placeholder
    /// exists AND the latest HTTP call (if any) succeeded.
    /// </summary>
    private async Task<bool> TryStreamingUpdateAsync(
        ChannelPendingChatSession session, string accumulated, bool isFinal)
    {
        var adapter = ResolvePlatformAdapter(session.Platform) as IStreamingPlatformAdapter;
        if (adapter is null)
            return false;

        if (!_streamingDeliveries.TryGetValue(session.SessionId, out var state))
        {
            state = new StreamingDeliveryState();
            _streamingDeliveries[session.SessionId] = state;
        }
        if (state.Disabled)
            return false;

        var nyxClient = Services.GetRequiredService<NyxIdApiClient>();
        var inbound = BuildInboundFromSession(session);
        var registration = BuildRegistrationFromSession(session);

        using var cts = new CancellationTokenSource(StreamingHttpTimeout);

        if (state.MessageId is null)
        {
            try
            {
                var messageId = await adapter.PostStreamingPlaceholderAsync(
                    accumulated, inbound, registration, nyxClient, cts.Token);
                if (string.IsNullOrEmpty(messageId))
                {
                    state.Disabled = true;
                    RecordDiagnostic("Stream:placeholder:failed", session.Platform, session.RegistrationId,
                        $"sessionId={session.SessionId}");
                    return false;
                }
                state.MessageId = messageId;
                state.LastEditAt = DateTimeOffset.UtcNow;
                state.EditCount = 1;
                RecordDiagnostic("Stream:placeholder:posted", session.Platform, session.RegistrationId,
                    $"sessionId={session.SessionId} messageId={messageId}");
                return true;
            }
            catch (Exception ex)
            {
                state.Disabled = true;
                Logger.LogWarning(ex, "Streaming placeholder post failed for session {SessionId}", session.SessionId);
                RecordDiagnostic("Stream:placeholder:error", session.Platform, session.RegistrationId,
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Intermediate deltas: honour throttle so we don't hammer the platform.
        // Still report the placeholder as "active" so callers treat the session
        // as streamed — the next delta or the final flush will catch up.
        if (!isFinal && DateTimeOffset.UtcNow - state.LastEditAt < StreamingEditThrottle)
            return true;

        try
        {
            var delivery = await adapter.UpdateStreamingMessageAsync(
                state.MessageId!, accumulated, inbound, registration, nyxClient, cts.Token);
            state.LastEditAt = DateTimeOffset.UtcNow;
            state.EditCount++;
            if (!delivery.Succeeded)
            {
                if (isFinal)
                {
                    state.Disabled = true;
                    Logger.LogWarning(
                        "Streaming final PATCH rejected: session={SessionId}, detail={Detail}",
                        session.SessionId, delivery.Detail);
                }
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Streaming update error for session {SessionId}", session.SessionId);
            if (isFinal)
            {
                state.Disabled = true;
                RecordDiagnostic("Stream:final:error", session.Platform, session.RegistrationId,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    private async Task<bool> TrySendDirectReplyAsync(
        ChannelPendingChatSession session,
        string replyText,
        string? detailPrefix = null)
    {
        var delivery = await SendPlatformReplyAsync(session, replyText);
        var detail = string.IsNullOrWhiteSpace(detailPrefix)
            ? delivery.Detail
            : $"{detailPrefix} {delivery.Detail}";

        if (delivery.Succeeded)
        {
            RecordDiagnostic("Reply:done", session.Platform, session.RegistrationId, detail);
            return true;
        }

        Logger.LogWarning("SendReply rejected: platform={Platform}, session={SessionId}, detail={Detail}",
            session.Platform,
            session.SessionId,
            detail);
        RecordDiagnostic("Reply:error", session.Platform, session.RegistrationId, detail);
        return false;
    }

    private IPlatformAdapter? ResolvePlatformAdapter(string platform)
    {
        var adapters = Services.GetRequiredService<IEnumerable<IPlatformAdapter>>();
        return adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));
    }

    private static InboundMessage BuildInboundFromSession(ChannelPendingChatSession session) =>
        new()
        {
            Platform = session.Platform,
            ConversationId = session.ConversationId,
            SenderId = session.SenderId,
            SenderName = session.SenderName,
            Text = string.Empty,
            MessageId = session.MessageId,
            ChatType = session.ChatType,
        };

    private static ChannelBotRegistrationEntry BuildRegistrationFromSession(ChannelPendingChatSession session) =>
        new()
        {
            Id = session.RegistrationId,
            Platform = session.Platform,
            NyxProviderSlug = session.NyxProviderSlug,
            NyxUserToken = session.OrgToken,
            ScopeId = session.RegistrationScopeId,
        };

    private sealed class StreamingDeliveryState
    {
        public string? MessageId;
        public DateTimeOffset LastEditAt;
        public bool Disabled;
        public int EditCount;
    }

    private async Task<bool> TryHandleAgentBuilderAsync(
        ChannelInboundEvent evt,
        string effectiveToken,
        CancellationToken ct)
    {
        if (!AgentBuilderCardFlow.TryResolve(evt, out var decision) || decision is null)
            return false;

        RecordDiagnostic("AgentBuilder:start", evt.Platform, evt.RegistrationId, decision.ToolAction ?? "card");

        var replyPayload = decision.ReplyPayload;
        if (decision.RequiresToolExecution)
        {
            var metadata = BuildAgentBuilderMetadata(evt, effectiveToken);
            var previousMetadata = AgentToolRequestContext.CurrentMetadata;
            try
            {
                AgentToolRequestContext.CurrentMetadata = metadata;
                var tool = ActivatorUtilities.CreateInstance<AgentBuilderTool>(Services);
                var toolResult = await tool.ExecuteAsync(decision.ToolArgumentsJson!, ct);
                replyPayload = AgentBuilderCardFlow.FormatToolResult(decision, toolResult);
            }
            finally
            {
                AgentToolRequestContext.CurrentMetadata = previousMetadata;
            }
        }

        var delivery = await SendDirectPlatformReplyAsync(evt, replyPayload, ct);
        if (!delivery.Succeeded)
            throw new InvalidOperationException($"Agent builder reply rejected: {delivery.Detail}");

        await PersistHandledMessageIdAsync(evt.MessageId, ct);
        RecordDiagnostic("AgentBuilder:done", evt.Platform, evt.RegistrationId, decision.ToolAction ?? "card");
        return true;
    }

    private IReadOnlyDictionary<string, string> BuildAgentBuilderMetadata(
        ChannelInboundEvent evt,
        string effectiveToken)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = effectiveToken,
            [LLMRequestMetadataKeys.NyxIdOrgToken] = evt.RegistrationToken,
            ["scope_id"] = evt.RegistrationScopeId,
            [ChannelMetadataKeys.Platform] = evt.Platform,
            [ChannelMetadataKeys.SenderId] = evt.SenderId,
            [ChannelMetadataKeys.SenderName] = evt.SenderName,
            [ChannelMetadataKeys.ConversationId] = evt.ConversationId,
            [ChannelMetadataKeys.MessageId] = evt.MessageId,
            [ChannelMetadataKeys.ChatType] = AgentBuilderCardFlow.ResolveToolChatType(evt),
        };
        return metadata;
    }

    private async Task<PlatformReplyDeliveryResult> SendDirectPlatformReplyAsync(
        ChannelInboundEvent evt,
        string replyText,
        CancellationToken ct)
    {
        var adapters = Services.GetRequiredService<IEnumerable<IPlatformAdapter>>();
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, evt.Platform, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
            return new PlatformReplyDeliveryResult(false, $"No adapter for platform: {evt.Platform}");

        var nyxClient = Services.GetRequiredService<NyxIdApiClient>();
        var inbound = new InboundMessage
        {
            Platform = evt.Platform,
            ConversationId = evt.ConversationId,
            SenderId = evt.SenderId,
            SenderName = evt.SenderName,
            Text = evt.Text,
            MessageId = evt.MessageId,
            ChatType = evt.ChatType,
            Extra = evt.Extra,
        };

        var registration = new ChannelBotRegistrationEntry
        {
            Id = evt.RegistrationId,
            Platform = evt.Platform,
            NyxProviderSlug = evt.NyxProviderSlug,
            NyxUserToken = evt.RegistrationToken,
            ScopeId = evt.RegistrationScopeId,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
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
        await PersistProcessedMessageIdAsync(session, ct);
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
        chatRequest.Metadata[ChannelMetadataKeys.ConversationId] = session.ConversationId;
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

    private async Task PersistProcessedMessageIdAsync(
        ChannelPendingChatSession session,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.MessageId) ||
            _processedMessageIds.Contains(session.MessageId))
        {
            return;
        }

        await PersistDomainEventAsync(new ChannelInboundDispatchedEvent
        {
            SessionId = session.SessionId,
            MessageId = session.MessageId,
        }, ct);

        TrackProcessedMessageId(session.MessageId);
    }

    private async Task PersistHandledMessageIdAsync(
        string? messageId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId) || _processedMessageIds.Contains(messageId))
            return;

        await PersistDomainEventAsync(new ChannelInboundHandledEvent
        {
            MessageId = messageId,
        }, ct);

        TrackProcessedMessageId(messageId);
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

        if (!string.IsNullOrWhiteSpace(evt.DisplayName))
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

    private static ChannelUserState ApplyInboundDispatched(ChannelUserState current, ChannelInboundDispatchedEvent evt)
        => ApplyProcessedMessageId(current, evt.MessageId);

    private static ChannelUserState ApplyInboundHandled(ChannelUserState current, ChannelInboundHandledEvent evt)
        => ApplyProcessedMessageId(current, evt.MessageId);

    private static ChannelUserState ApplyProcessedMessageId(ChannelUserState current, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return current;

        var next = current.Clone();
        if (!next.ProcessedMessageIds.Contains(messageId))
        {
            next.ProcessedMessageIds.Add(messageId);
            while (next.ProcessedMessageIds.Count > MaxProcessedMessageIds)
                next.ProcessedMessageIds.RemoveAt(0);
        }

        return next;
    }

    private static ChannelUserState ApplyCompleted(ChannelUserState current, ChannelChatCompletedEvent evt)
    {
        var next = current.Clone();
        next.PendingSessions.Remove(evt.SessionId);
        return next;
    }

    private void RecordDiagnostic(string stage, string platform, string registrationId, string? detail = null)
    {
        Services.GetService<IChannelRuntimeDiagnostics>()?.Record(stage, platform, registrationId, detail);
    }
}
