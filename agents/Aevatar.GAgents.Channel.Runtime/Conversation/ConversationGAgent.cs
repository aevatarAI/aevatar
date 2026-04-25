using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-conversation single-activation actor keyed by <see cref="ConversationReference.CanonicalKey"/>.
/// Owns conversation-scoped dedup state and is the authoritative boundary for atomic
/// "admit activity → invoke bot turn → commit outbound + dedup entry" semantics per RFC §5.2b.
/// </summary>
/// <remarks>
/// <para>
/// Dedup is strongly serialized by the actor turn. The pipeline may fast-path-check
/// <see cref="ConversationGAgentState.ProcessedMessageIds"/> upstream, but the authoritative
/// check lives inside <see cref="HandleInboundActivityAsync"/>. Double delivery of the same
/// <see cref="ChatActivity.Id"/> is collapsed to one emitted <see cref="ConversationTurnCompletedEvent"/>.
/// </para>
/// <para>
/// Downstream projections subscribe through the standard
/// <see cref="Aevatar.Foundation.Abstractions.CommittedStateEventPublished"/> pipeline wired up by
/// <see cref="GAgentBase{TState}.PersistDomainEventAsync{TEvent}"/>. No inline projection writes.
/// </para>
/// </remarks>
public sealed partial class ConversationGAgent : GAgentBase<ConversationGAgentState>
{
    // Orleans Reminders (the durable scheduler backing ScheduleSelfDurableTimeoutAsync)
    // round dueTime up to the local reminder service tick (typically ~1 minute), so
    // sub-minute schedules are unreliable. The inbox dispatch happens inline via
    // IChannelLlmReplyInbox; the durable timer is reserved for retry/rehydration.
    private static readonly TimeSpan DeferredLlmDispatchRetryDelay = TimeSpan.FromSeconds(60);
    // Pending LLM reply requests older than this are considered stale on rehydration:
    // the user gave up, the relay reply_token (~30 min TTL) is likely already expired,
    // and the user access token (~15 min TTL) used for the LLM call is definitely gone.
    // Drop them rather than burn an LLM round and reply hours late.
    private static readonly TimeSpan PendingLlmReplyRequestMaxAge = TimeSpan.FromMinutes(5);

    // Mirror of DeferredLlmDispatchRetryDelay for the inbound-turn retry pipeline.
    // The same reminder-granularity floor applies: any requested retry shorter than this
    // would be silently rounded up by Orleans and appear lost.
    private static readonly TimeSpan DeferredInboundTurnRetryDelay = TimeSpan.FromSeconds(60);
    // Bounded retry count for transient inbound-turn failures. On exhaustion the actor
    // persists a terminal ConversationContinueFailedEvent (NotRetryable) so the pending
    // set does not grow unboundedly.
    public const int MaxInboundTurnRetryCount = 5;
    private readonly Dictionary<string, NyxRelayReplyTokenContext> _nyxRelayReplyTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NyxRelayStreamingState> _nyxRelayStreamingStates = new(StringComparer.Ordinal);

    /// <summary>
    /// Actor-scoped, in-memory streaming state for one conversation turn. Never persisted: tracks
    /// the upstream platform message id of the placeholder send and the two distinct failure
    /// modes that can disable parts of the streaming path. Keyed by <c>correlation_id</c>, same
    /// lifecycle as <see cref="NyxRelayReplyTokenContext"/>.
    /// </summary>
    /// <remarks>
    /// The two failure flags carry different semantics with respect to the NyxID reply token:
    /// <list type="bullet">
    /// <item><c>Disabled</c> means streaming was aborted <em>before</em> any successful send, so
    /// the reply token is still available and the actor may safely fall back to a single-shot
    /// <c>/reply</c> via <see cref="IConversationTurnRunner.RunLlmReplyAsync"/>.</item>
    /// <item><c>SuppressInterim</c> means the first chunk already consumed the reply token (the
    /// placeholder or first delta landed) but a later interim edit failed. The final edit must
    /// still be attempted via <c>/reply/update</c>; falling back to <c>/reply</c> would reuse a
    /// dead token and turn the partial into the user-visible terminal state.</item>
    /// </list>
    /// </remarks>
    private sealed record NyxRelayStreamingState(
        string? PlatformMessageId,
        string LastFlushedText,
        int EditCount,
        bool Disabled,
        bool SuppressInterim)
    {
        public static NyxRelayStreamingState Initial { get; } = new(null, string.Empty, 0, false, false);

        /// <summary>
        /// True once the first successful send has landed: the NyxID reply token has been
        /// consumed and any further outbound must go through <c>/reply/update</c>. Used as the
        /// "token is dead, don't fall back to <c>/reply</c>" guard.
        /// </summary>
        public bool ReplyTokenConsumed => !string.IsNullOrEmpty(PlatformMessageId);
    }

    /// <summary>
    /// Sliding window cap on retained processed ids. Keeps state size bounded while still
    /// catching typical redelivery windows (seconds to minutes).
    /// </summary>
    public const int ProcessedIdsCap = 10000;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await SchedulePendingLlmReplyDispatchesAsync(ct);
        await SchedulePendingInboundTurnRetriesAsync(ct);
    }

    /// <inheritdoc />
    protected override ConversationGAgentState TransitionState(ConversationGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationTurnCompletedEvent>(ApplyTurnCompleted)
            .On<NeedsLlmReplyEvent>(ApplyLlmReplyRequested)
            .On<ConversationContinueRejectedEvent>(ApplyContinueRejected)
            .On<ConversationContinueFailedEvent>(ApplyContinueFailed)
            .On<InboundTurnRetryScheduledEvent>(ApplyInboundTurnRetryScheduled)
            .OrCurrent();

    /// <summary>
    /// Authoritative inbound admission: dedup + run bot turn + commit atomically.
    /// </summary>
    [EventHandler]
    public Task HandleInboundActivityAsync(ChatActivity activity) =>
        HandleInboundActivityCoreAsync(activity, ConversationTurnRuntimeContext.Empty);

    [EventHandler]
    public async Task HandleNyxRelayInboundActivityAsync(NyxRelayInboundActivity relayActivity)
    {
        ArgumentNullException.ThrowIfNull(relayActivity);

        var activity = relayActivity.Activity?.Clone() ?? new ChatActivity();
        var runtimeContext = CaptureNyxRelayReplyToken(relayActivity, activity);
        if (runtimeContext.NyxRelayReplyToken is { } tokenContext)
            await ScheduleNyxRelayReplyTokenCleanupAsync(tokenContext, CancellationToken.None);
        await HandleInboundActivityCoreAsync(activity, runtimeContext);
    }

    private async Task HandleInboundActivityCoreAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            Logger.LogWarning("Dropping ChatActivity with empty id (conversation={Key})",
                activity.Conversation?.CanonicalKey);
            return;
        }

        if (State.ProcessedMessageIds.Contains(activity.Id))
        {
            Logger.LogInformation(
                "Duplicate inbound activity {ActivityId} (conversation={Key}); skipping turn",
                activity.Id, activity.Conversation?.CanonicalKey);
            RemoveNyxRelayReplyToken(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity);
            return;
        }

        var runner = ResolveRunner();
        var result = await runner.RunInboundAsync(activity, runtimeContext, CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.LlmReplyRequest is not null)
        {
            // The transient inbox copy keeps reply_token + expiry so the LLM worker can
            // echo them back inside LlmReplyReadyEvent; the persisted state copy must
            // not carry the credential into the event store / projection / read model.
            var inboxCopy = result.LlmReplyRequest.Clone();
            inboxCopy.TargetActorId = Id;
            var persistedCopy = inboxCopy.Clone();
            persistedCopy.ReplyToken = string.Empty;
            persistedCopy.ReplyTokenExpiresAtUnixMs = 0;
            await PersistDomainEventAsync(persistedCopy);
            await DispatchPendingLlmReplyAsync(inboxCopy, CancellationToken.None);
            Logger.LogInformation(
                "Accepted inbound activity for deferred LLM reply: activity={ActivityId} conversation={Key}",
                activity.Id,
                activity.Conversation?.CanonicalKey);
            return;
        }

        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = activity.Id,
                CausationCommandId = string.Empty,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = result.AuthPrincipal,
                Conversation = activity.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? new MessageContent(),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            await PersistDomainEventAsync(completed);
            RemoveNyxRelayReplyToken(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity);
            Logger.LogInformation(
                "Completed inbound turn: activity={ActivityId} sent={SentId} conversation={Key}",
                activity.Id, result.SentActivityId, activity.Conversation?.CanonicalKey);
            return;
        }

        if (result.FailureKind == FailureKind.TransientAdapterError)
        {
            await HandleInboundTurnTransientFailureAsync(activity, runtimeContext, result, nowMs);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = string.Empty,
            CorrelationId = activity.Id,
            CausationId = string.Empty,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        await PersistDomainEventAsync(failed);
        RemoveNyxRelayReplyToken(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity);
        Logger.LogWarning(
            "Inbound turn failed: activity={ActivityId} code={Code} kind={Kind}",
            activity.Id, result.ErrorCode, result.FailureKind);
    }

    /// <summary>
    /// Mirrors the deferred LLM reply retry pattern for the inbound-turn path: bounds the retry
    /// count, schedules a durable reminder for the next attempt, or emits a terminal
    /// <see cref="ConversationContinueFailedEvent"/> on exhaustion so the pending entry is
    /// reaped by the state matcher.
    /// </summary>
    private async Task HandleInboundTurnTransientFailureAsync(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext,
        ConversationTurnResult result,
        long nowMs)
    {
        var existingPending = FindPendingInboundTurn(activity.Id);
        var nextRetryCount = (existingPending?.RetryCount ?? 0) + 1;

        if (nextRetryCount > MaxInboundTurnRetryCount)
        {
            var failed = new ConversationContinueFailedEvent
            {
                CommandId = string.Empty,
                CorrelationId = activity.Id,
                CausationId = string.Empty,
                Kind = FailureKind.TransientAdapterError,
                ErrorCode = string.IsNullOrWhiteSpace(result.ErrorCode)
                    ? "inbound_turn_retries_exhausted"
                    : result.ErrorCode,
                ErrorSummary = string.IsNullOrWhiteSpace(result.ErrorSummary)
                    ? "Inbound turn retries exhausted."
                    : result.ErrorSummary,
                NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
                FailedAtUnixMs = nowMs,
            };
            await PersistDomainEventAsync(failed);
            RemoveNyxRelayReplyToken(runtimeContext.NyxRelayReplyToken?.CorrelationId, activity);
            Logger.LogWarning(
                "Inbound turn retries exhausted: activity={ActivityId} retryCount={RetryCount} code={Code}",
                activity.Id,
                nextRetryCount - 1,
                result.ErrorCode);
            return;
        }

        var requested = result.RetryAfter ?? DeferredInboundTurnRetryDelay;
        // Floor to reminder granularity so the durable scheduler does not silently round the
        // request up past the retry window and drop the dispatch (same trap the LLM reply
        // retry path has to guard against).
        var retryAfter = requested < DeferredInboundTurnRetryDelay
            ? DeferredInboundTurnRetryDelay
            : requested;
        var firstFailedUnixMs = existingPending is { FirstFailedUnixMs: > 0 }
            ? existingPending.FirstFailedUnixMs
            : nowMs;
        var nextRetryUnixMs = DateTimeOffset.UtcNow.Add(retryAfter).ToUnixTimeMilliseconds();

        var scheduled = new InboundTurnRetryScheduledEvent
        {
            ActivityId = activity.Id,
            Activity = activity.Clone(),
            RetryCount = nextRetryCount,
            FirstFailedUnixMs = firstFailedUnixMs,
            NextRetryUnixMs = nextRetryUnixMs,
            ScheduledAtUnixMs = nowMs,
        };
        await PersistDomainEventAsync(scheduled);
        await ScheduleDeferredInboundTurnRetryAsync(activity.Id, retryAfter, CancellationToken.None);

        Logger.LogInformation(
            "Scheduled inbound turn retry: activity={ActivityId} retryCount={RetryCount} retryAfter={RetryAfter} code={Code}",
            activity.Id,
            nextRetryCount,
            retryAfter,
            result.ErrorCode);
    }

    [EventHandler]
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync(DeferredLlmReplyDispatchRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var pendingRequest = FindPendingLlmReplyRequest(evt.CorrelationId);
        if (pendingRequest is null)
        {
            Logger.LogDebug(
                "Ignoring deferred LLM dispatch trigger without pending request: correlation={CorrelationId}",
                evt.CorrelationId);
            return;
        }

        await DispatchPendingLlmReplyAsync(pendingRequest, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDeferredLlmReplyDroppedAsync(DeferredLlmReplyDroppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var pending = FindPendingLlmReplyRequest(evt.CorrelationId);
        if (pending is null)
        {
            Logger.LogDebug(
                "Ignoring deferred LLM reply drop without pending request: correlation={CorrelationId} reason={Reason}",
                evt.CorrelationId,
                evt.Reason);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(evt.Reason) ? "deferred_llm_reply_dropped" : evt.Reason;
        var failed = new ConversationContinueFailedEvent
        {
            CommandId = BuildLlmReplyCommandId(evt.CorrelationId),
            CorrelationId = evt.CorrelationId,
            CausationId = string.Empty,
            Kind = FailureKind.PermanentAdapterError,
            ErrorCode = reason,
            ErrorSummary = "Deferred LLM reply request was dropped by the inbox pre-LLM gate.",
            NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
            FailedAtUnixMs = evt.DroppedAtUnixMs > 0
                ? evt.DroppedAtUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await PersistDomainEventAsync(failed);
        RemoveNyxRelayReplyToken(evt.CorrelationId, pending.Activity);

        Logger.LogInformation(
            "Retired pending LLM reply after inbox drop: correlation={CorrelationId} reason={Reason}",
            evt.CorrelationId,
            reason);
    }

    [EventHandler]
    public async Task HandleDeferredInboundTurnRetryRequestedAsync(DeferredInboundTurnRetryRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var pending = FindPendingInboundTurn(evt.ActivityId);
        if (pending is null || pending.Activity is null)
        {
            // Pending entry already reaped — either by ApplyTurnCompleted (success), the
            // terminal NotRetryable ApplyContinueFailed (exhaustion), or ApplyLlmReplyRequested
            // (redelivery accepted into the LLM reply pipeline before this retry could fire).
            Logger.LogDebug(
                "Ignoring deferred inbound turn retry without pending entry: activity={ActivityId}",
                evt.ActivityId);
            return;
        }

        // The in-memory _nyxRelayReplyTokens dict is the authoritative source for the relay
        // reply credential. If the activation is still alive, BuildNyxRelayRuntimeContext
        // will re-hydrate it from activity.outbound_delivery.correlation_id; if the pod was
        // restarted between attempts the dict is empty and the retry runs with Empty
        // context. In both cases the runner is invoked identically to the first turn.
        var runtimeContext = BuildNyxRelayRuntimeContext(
            pending.Activity.OutboundDelivery?.CorrelationId,
            pending.Activity);

        await HandleInboundActivityCoreAsync(pending.Activity.Clone(), runtimeContext);
    }

    private async Task DispatchPendingLlmReplyAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        var inbox = Services.GetService<IChannelLlmReplyInbox>();
        if (inbox is null)
        {
            Logger.LogWarning(
                "Channel LLM reply inbox not registered; scheduling durable retry: correlation={CorrelationId}",
                request.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(request, DeferredLlmDispatchRetryDelay, ct);
            return;
        }

        // Retry and rehydration paths read `request` from State.PendingLlmReplyRequests,
        // which always carries an empty ReplyToken (the inbound handler strips it before
        // persist). If the actor is still alive and the in-memory dict still has the
        // token for this correlation, re-enrich the inbox copy so the subscriber's relay
        // credential gate does not mistake a legitimate retry for a dead request.
        var enriched = EnrichWithRuntimeReplyTokenIfNeeded(request);

        try
        {
            await inbox.EnqueueAsync(enriched.Clone(), ct);
            Logger.LogInformation(
                "Enqueued LLM reply request to inbox: correlation={CorrelationId} conversation={Key} replyTokenSource={Source}",
                enriched.CorrelationId,
                enriched.Activity?.Conversation?.CanonicalKey,
                DescribeEnqueuedReplyTokenSource(request, enriched));
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to enqueue LLM reply request; scheduling durable retry: correlation={CorrelationId}",
                request.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(request, DeferredLlmDispatchRetryDelay, ct);
        }
    }

    private NeedsLlmReplyEvent EnrichWithRuntimeReplyTokenIfNeeded(NeedsLlmReplyEvent request)
    {
        if (!string.IsNullOrWhiteSpace(request.ReplyToken))
            return request;

        var correlationId = NormalizeOptional(request.Activity?.OutboundDelivery?.CorrelationId) ??
                            NormalizeOptional(request.CorrelationId);
        if (correlationId is null)
            return request;

        if (!_nyxRelayReplyTokens.TryGetValue(correlationId, out var tokenContext))
            return request;

        if (tokenContext.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _nyxRelayReplyTokens.Remove(correlationId);
            return request;
        }

        var enriched = request.Clone();
        enriched.ReplyToken = tokenContext.ReplyToken;
        enriched.ReplyTokenExpiresAtUnixMs = tokenContext.ExpiresAtUtc.ToUnixTimeMilliseconds();
        return enriched;
    }

    private static string DescribeEnqueuedReplyTokenSource(
        NeedsLlmReplyEvent original,
        NeedsLlmReplyEvent enriched)
    {
        if (!string.IsNullOrWhiteSpace(original.ReplyToken))
            return "inbound-direct";
        if (!string.IsNullOrWhiteSpace(enriched.ReplyToken))
            return "actor-runtime-dict";
        return "none";
    }

    [EventHandler]
    public async Task HandleLlmReplyReadyAsync(LlmReplyReadyEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var commandId = BuildLlmReplyCommandId(evt.CorrelationId);
        var pendingRequest = FindPendingLlmReplyRequest(evt.CorrelationId);
        if (State.ProcessedCommandIds.Contains(commandId))
        {
            Logger.LogInformation(
                "Duplicate LLM reply ready event {CorrelationId} (conversation={Key}); skipping outbound",
                evt.CorrelationId,
                State.Conversation?.CanonicalKey);
            return;
        }

        var referenceActivity = pendingRequest?.Activity ?? evt.Activity;
        var runtimeContext = BuildNyxRelayRuntimeContextForReply(evt, pendingRequest?.Activity);
        Logger.LogInformation(
            "Received LLM reply ready: correlation={CorrelationId} terminal={TerminalState} replyTokenSource={Source}",
            evt.CorrelationId,
            evt.TerminalState,
            DescribeReplyTokenSource(evt, runtimeContext));

        if (await TryCompleteStreamedReplyAsync(evt, commandId, referenceActivity, runtimeContext))
            return;

        var runner = ResolveRunner();
        var result = await runner.RunLlmReplyAsync(
            evt,
            runtimeContext,
            CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = string.Empty,
                CausationCommandId = commandId,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = string.IsNullOrWhiteSpace(result.AuthPrincipal) ? "bot" : result.AuthPrincipal,
                Conversation = evt.Activity?.Conversation?.Clone() ?? State.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? evt.Outbound?.Clone() ?? new MessageContent(),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            await PersistDomainEventAsync(completed);
            RemoveNyxRelayReplyToken(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity);
            Logger.LogInformation(
                "Completed deferred LLM reply: correlation={CorrelationId} sent={SentId} conversation={Key}",
                evt.CorrelationId,
                result.SentActivityId,
                completed.Conversation?.CanonicalKey);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = commandId,
            CorrelationId = evt.CorrelationId,
            CausationId = string.Empty,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        await PersistDomainEventAsync(failed);
        SweepExpiredNyxRelayReplyTokens();
        if (failed.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
            RemoveNyxRelayReplyToken(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity);
        if (failed.RetryPolicyCase != ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable &&
            pendingRequest is not null)
        {
            var requested = failed.RetryAfterMs > 0
                ? TimeSpan.FromMilliseconds(failed.RetryAfterMs)
                : DeferredLlmDispatchRetryDelay;
            // Floor the retry delay to the durable scheduler's reliable granularity. Orleans
            // Reminders effectively round sub-minute schedules up to the next tick, so any
            // shorter requested delay would silently miss; honour at least DeferredLlmDispatchRetryDelay.
            var retryAfter = requested < DeferredLlmDispatchRetryDelay
                ? DeferredLlmDispatchRetryDelay
                : requested;
            await ScheduleDeferredLlmReplyDispatchAsync(
                pendingRequest,
                retryAfter,
                CancellationToken.None);
        }
        Logger.LogWarning(
            "Deferred LLM reply failed: correlation={CorrelationId} code={Code} kind={Kind}",
            evt.CorrelationId,
            result.ErrorCode,
            result.FailureKind);
    }

    /// <summary>
    /// Drives one progressive streaming delta: placeholder send on the first chunk, edit-in-place
    /// on subsequent chunks. Runs inside the actor turn so the reply token stays within the actor
    /// boundary and the edit ordering is enforced by actor serialization.
    /// </summary>
    [EventHandler]
    public async Task HandleLlmReplyStreamChunkAsync(LlmReplyStreamChunkEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null || evt.Activity is null || string.IsNullOrWhiteSpace(evt.AccumulatedText))
        {
            Logger.LogDebug(
                "Dropping malformed streaming chunk: correlation={CorrelationId}",
                evt.CorrelationId);
            return;
        }

        var state = _nyxRelayStreamingStates.GetValueOrDefault(correlationId) ?? NyxRelayStreamingState.Initial;
        if (state.Disabled || state.SuppressInterim)
            return;

        if (State.ProcessedCommandIds.Contains(BuildLlmReplyCommandId(evt.CorrelationId)))
        {
            // Turn already finalized; drop any late chunk that sneaks in via the actor inbox.
            return;
        }

        var runtimeContext = BuildNyxRelayRuntimeContext(evt.CorrelationId, evt.Activity);
        if (runtimeContext.NyxRelayReplyToken is null)
        {
            Logger.LogInformation(
                "Streaming chunk received but relay reply token is unavailable; disabling streaming for turn. correlation={CorrelationId}",
                evt.CorrelationId);
            _nyxRelayStreamingStates[correlationId] = state with { Disabled = true };
            return;
        }

        var runner = ResolveRunner();
        var result = await runner.RunStreamChunkAsync(
            evt,
            state.PlatformMessageId,
            runtimeContext,
            CancellationToken.None);
        if (!result.Success)
        {
            if (state.ReplyTokenConsumed)
            {
                // First chunk already consumed the reply token. Skip further interim edits but
                // preserve PlatformMessageId so the final edit on LlmReplyReady can still try
                // to reconcile the user-visible message. Falling back to /reply would reuse a
                // dead token.
                Logger.LogInformation(
                    "Streaming interim edit failed after token consumed; suppressing interim edits, final edit will still be attempted. correlation={CorrelationId}, code={Code}, editUnsupported={EditUnsupported}",
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.EditUnsupported);
                _nyxRelayStreamingStates[correlationId] = state with { SuppressInterim = true };
            }
            else
            {
                // First send itself failed, so the reply token is still usable. Let
                // LlmReplyReady fall back to a single-shot /reply via RunLlmReplyAsync.
                Logger.LogInformation(
                    "Streaming initial send failed before token consumed; disabling streaming and allowing /reply fallback. correlation={CorrelationId}, code={Code}, editUnsupported={EditUnsupported}",
                    evt.CorrelationId,
                    result.ErrorCode,
                    result.EditUnsupported);
                _nyxRelayStreamingStates[correlationId] = state with { Disabled = true };
            }
            return;
        }

        var isFirstChunk = string.IsNullOrEmpty(state.PlatformMessageId);
        var newPlatformMessageId = string.IsNullOrWhiteSpace(result.PlatformMessageId)
            ? state.PlatformMessageId
            : result.PlatformMessageId;
        _nyxRelayStreamingStates[correlationId] = state with
        {
            PlatformMessageId = newPlatformMessageId,
            LastFlushedText = evt.AccumulatedText,
            EditCount = isFirstChunk ? 0 : state.EditCount + 1,
        };
    }

    private async Task<bool> TryCompleteStreamedReplyAsync(
        LlmReplyReadyEvent evt,
        string commandId,
        ChatActivity? referenceActivity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        if (evt.TerminalState != LlmReplyTerminalState.Completed)
            return false;

        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is null)
            return false;

        if (!_nyxRelayStreamingStates.TryGetValue(correlationId, out var state))
            return false;
        // Disabled means the initial send never landed, so the reply token is still usable
        // and the caller may fall back to a single-shot /reply. A missing PlatformMessageId
        // with SuppressInterim would be inconsistent, but treat it the same for safety.
        if (state.Disabled || string.IsNullOrEmpty(state.PlatformMessageId))
            return false;

        var platformMessageId = state.PlatformMessageId!;
        var finalText = evt.Outbound?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(finalText))
        {
            // Streaming rendered something partial but the LLM reported empty; the reply token
            // is dead (first chunk consumed it), so we cannot fall back to /reply. Accept the
            // last flushed text as the terminal user-visible state rather than spinning on a
            // dead token.
            Logger.LogWarning(
                "Streaming LLM reply final text was empty; persisting last flushed partial as terminal. correlation={CorrelationId} platformMessageId={PlatformMessageId}",
                evt.CorrelationId,
                platformMessageId);
            await PersistStreamedCompletionAsync(evt, commandId, referenceActivity, platformMessageId, state.LastFlushedText, state.EditCount);
            return true;
        }

        var edits = state.EditCount;
        if (!string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal))
        {
            var runner = ResolveRunner();
            var finalChunk = new LlmReplyStreamChunkEvent
            {
                CorrelationId = evt.CorrelationId,
                RegistrationId = evt.RegistrationId,
                Activity = referenceActivity?.Clone() ?? evt.Activity?.Clone() ?? new ChatActivity(),
                AccumulatedText = finalText,
                ChunkAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var finalResult = await runner.RunStreamChunkAsync(
                finalChunk,
                platformMessageId,
                runtimeContext,
                CancellationToken.None);
            if (!finalResult.Success)
            {
                // The reply token was already consumed by the first chunk, so falling back to
                // a fresh /reply via RunLlmReplyAsync would reuse a dead JTI and surface as 401
                // to the user. Persist the last flushed partial as the terminal state instead —
                // the user sees the stale partial, but we don't spin on a guaranteed-failing
                // send. Retries cannot help here.
                Logger.LogWarning(
                    "Streaming final flush failed after token consumed; persisting last flushed partial as terminal. correlation={CorrelationId}, code={Code}, platformMessageId={PlatformMessageId}",
                    evt.CorrelationId,
                    finalResult.ErrorCode,
                    platformMessageId);
                await PersistStreamedCompletionAsync(evt, commandId, referenceActivity, platformMessageId, state.LastFlushedText, state.EditCount);
                return true;
            }
            edits += 1;
        }

        await PersistStreamedCompletionAsync(evt, commandId, referenceActivity, platformMessageId, finalText, edits);
        return true;
    }

    private async Task PersistStreamedCompletionAsync(
        LlmReplyReadyEvent evt,
        string commandId,
        ChatActivity? referenceActivity,
        string platformMessageId,
        string outboundText,
        int edits)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = string.Empty,
            CausationCommandId = commandId,
            SentActivityId = $"nyx-relay-stream:{platformMessageId}",
            AuthPrincipal = "bot",
            Conversation = evt.Activity?.Conversation?.Clone()
                           ?? State.Conversation?.Clone()
                           ?? new ConversationReference(),
            Outbound = new MessageContent { Text = outboundText },
            CompletedAtUnixMs = nowMs,
            OutboundDelivery = ToOutboundDeliveryReceipt(evt.Activity?.OutboundDelivery),
        };
        await PersistDomainEventAsync(completed);
        RemoveNyxRelayReplyToken(evt.CorrelationId, referenceActivity);
        Logger.LogInformation(
            "Completed streamed LLM reply: correlation={CorrelationId} platformMessageId={PlatformMessageId} edits={EditCount} conversation={Key}",
            evt.CorrelationId,
            platformMessageId,
            edits,
            completed.Conversation?.CanonicalKey);
    }

    [EventHandler]
    public Task HandleNyxRelayReplyTokenCleanupRequestedAsync(NyxRelayReplyTokenCleanupRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var correlationId = NormalizeOptional(evt.CorrelationId);
        if (correlationId is not null &&
            _nyxRelayReplyTokens.TryGetValue(correlationId, out var tokenContext) &&
            tokenContext.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _nyxRelayReplyTokens.Remove(correlationId);
        }

        SweepExpiredNyxRelayReplyTokens();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Proactive command path: dedup by command id, optionally reject, otherwise invoke bot turn.
    /// </summary>
    [EventHandler]
    public async Task HandleContinueCommandAsync(ConversationContinueRequestedEvent cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (string.IsNullOrWhiteSpace(cmd.CommandId))
        {
            await EmitRejectAsync(cmd, RejectReason.Unspecified, "empty command_id");
            return;
        }

        if (State.ProcessedCommandIds.Contains(cmd.CommandId))
        {
            Logger.LogInformation(
                "Duplicate continue command {CommandId}; emitting DuplicateCommand rejection",
                cmd.CommandId);
            await EmitRejectAsync(cmd, RejectReason.DuplicateCommand, "duplicate command id");
            return;
        }

        var runner = ResolveRunner();
        var result = await runner.RunContinueAsync(cmd, CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.Success)
        {
            var completed = new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = string.Empty,
                CausationCommandId = cmd.CommandId,
                SentActivityId = result.SentActivityId,
                AuthPrincipal = string.IsNullOrEmpty(result.AuthPrincipal)
                    ? AuthPrincipalForContinue(cmd)
                    : result.AuthPrincipal,
                Conversation = cmd.Conversation?.Clone() ?? new ConversationReference(),
                Outbound = result.Outbound?.Clone() ?? (cmd.Payload?.Clone() ?? new MessageContent()),
                CompletedAtUnixMs = nowMs,
                OutboundDelivery = ToOutboundDeliveryReceipt(result.OutboundDelivery),
            };
            await PersistDomainEventAsync(completed);
            Logger.LogInformation(
                "Completed continue command: cmd={CommandId} sent={SentId} conversation={Key}",
                cmd.CommandId, result.SentActivityId, cmd.Conversation?.CanonicalKey);
            return;
        }

        var failed = new ConversationContinueFailedEvent
        {
            CommandId = cmd.CommandId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.CausationId,
            Kind = result.FailureKind,
            ErrorCode = result.ErrorCode,
            ErrorSummary = result.ErrorSummary,
            FailedAtUnixMs = nowMs,
        };
        AssignRetryPolicy(failed, result);
        await PersistDomainEventAsync(failed);
        Logger.LogWarning(
            "Continue command failed: cmd={CommandId} code={Code} kind={Kind}",
            cmd.CommandId, result.ErrorCode, result.FailureKind);
    }

    // Retry policy is driven by FailureKind, not by whether the caller supplied a backoff.
    // Only PermanentAdapterError terminates the command id; every other kind is retriable and
    // carries the supplied retry_after_ms (0 when omitted). This preserves transient recovery
    // paths even when runners report a transient failure without an explicit backoff.
    private static void AssignRetryPolicy(ConversationContinueFailedEvent failed, ConversationTurnResult result)
    {
        if (result.FailureKind == FailureKind.PermanentAdapterError)
        {
            failed.NotRetryable = new Google.Protobuf.WellKnownTypes.Empty();
            return;
        }

        failed.RetryAfterMs = result.RetryAfter is { } retry
            ? (long)retry.TotalMilliseconds
            : 0;
    }

    private static string AuthPrincipalForContinue(ConversationContinueRequestedEvent cmd) =>
        cmd.Kind == PrincipalKind.OnBehalfOfUser
            ? $"user:{cmd.OnBehalfOfUserId}"
            : "bot";

    private static string BuildLlmReplyCommandId(string? correlationId) =>
        $"llm:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildDeferredLlmReplyCallbackId(string? correlationId) =>
        $"conversation-llm-dispatch:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildNyxRelayReplyTokenCleanupCallbackId(string? correlationId) =>
        $"nyx-relay-reply-token-cleanup:{correlationId?.Trim() ?? string.Empty}";

    private static string BuildDeferredInboundTurnRetryCallbackId(string? activityId) =>
        $"conversation-inbound-turn-retry:{activityId?.Trim() ?? string.Empty}";

    private async Task ScheduleDeferredLlmReplyDispatchAsync(
        NeedsLlmReplyEvent request,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        await ScheduleSelfDurableTimeoutAsync(
            BuildDeferredLlmReplyCallbackId(request.CorrelationId),
            dueTime <= TimeSpan.Zero ? DeferredLlmDispatchRetryDelay : dueTime,
            new DeferredLlmReplyDispatchRequestedEvent
            {
                CorrelationId = request.CorrelationId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task ScheduleDeferredInboundTurnRetryAsync(
        string activityId,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        await ScheduleSelfDurableTimeoutAsync(
            BuildDeferredInboundTurnRetryCallbackId(activityId),
            dueTime <= TimeSpan.Zero ? DeferredInboundTurnRetryDelay : dueTime,
            new DeferredInboundTurnRetryRequestedEvent
            {
                ActivityId = activityId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task SchedulePendingInboundTurnRetriesAsync(CancellationToken ct)
    {
        // Snapshot to avoid enumerating the live repeated field while downstream scheduling
        // may trigger state mutations (the same invariant SchedulePendingLlmReplyDispatchesAsync
        // already relies on).
        var pending = State.PendingInboundTurns.ToArray();
        if (pending.Length == 0)
            return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entry in pending)
        {
            if (string.IsNullOrWhiteSpace(entry.ActivityId))
                continue;

            var remainingMs = entry.NextRetryUnixMs > 0
                ? entry.NextRetryUnixMs - nowMs
                : 0;
            var delay = remainingMs > 0
                ? TimeSpan.FromMilliseconds(remainingMs)
                : DeferredInboundTurnRetryDelay;
            if (delay < DeferredInboundTurnRetryDelay)
                delay = DeferredInboundTurnRetryDelay;

            await ScheduleDeferredInboundTurnRetryAsync(entry.ActivityId, delay, ct);
        }
    }

    private Task ScheduleNyxRelayReplyTokenCleanupAsync(NyxRelayReplyTokenContext tokenContext, CancellationToken ct)
    {
        var dueTime = tokenContext.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromSeconds(1);

        return ScheduleSelfDurableTimeoutAsync(
            BuildNyxRelayReplyTokenCleanupCallbackId(tokenContext.CorrelationId),
            dueTime,
            new NyxRelayReplyTokenCleanupRequestedEvent
            {
                CorrelationId = tokenContext.CorrelationId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task SchedulePendingLlmReplyDispatchesAsync(CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var maxAgeMs = (long)PendingLlmReplyRequestMaxAge.TotalMilliseconds;

        // Snapshot: PersistDomainEventAsync below mutates State.PendingLlmReplyRequests
        // via the state matcher, which would invalidate the iterator if we walked the
        // live collection.
        var pending = State.PendingLlmReplyRequests.ToArray();
        foreach (var request in pending)
        {
            var ageMs = request.RequestedAtUnixMs > 0 ? nowMs - request.RequestedAtUnixMs : 0;
            if (request.RequestedAtUnixMs > 0 && ageMs > maxAgeMs)
            {
                Logger.LogInformation(
                    "Dropping stale pending LLM reply request on rehydration: correlation={CorrelationId} ageMs={AgeMs}",
                    request.CorrelationId,
                    ageMs);
                var failed = new ConversationContinueFailedEvent
                {
                    CommandId = BuildLlmReplyCommandId(request.CorrelationId),
                    CorrelationId = request.CorrelationId,
                    CausationId = string.Empty,
                    Kind = FailureKind.PermanentAdapterError,
                    ErrorCode = "stale_pending_request_dropped",
                    ErrorSummary = "Pending LLM reply request exceeded max age and was dropped on actor rehydration.",
                    NotRetryable = new Google.Protobuf.WellKnownTypes.Empty(),
                    FailedAtUnixMs = nowMs,
                };
                await PersistDomainEventAsync(failed);
                continue;
            }

            await DispatchPendingLlmReplyAsync(request, ct);
        }
    }

    private Task EmitRejectAsync(ConversationContinueRequestedEvent cmd, RejectReason reason, string detail)
    {
        var rejected = new ConversationContinueRejectedEvent
        {
            CommandId = cmd.CommandId,
            CorrelationId = cmd.CorrelationId,
            CausationId = cmd.CausationId,
            Reason = reason,
            ReasonDetail = detail,
            RejectedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return PersistDomainEventAsync(rejected);
    }

    private IConversationTurnRunner ResolveRunner() =>
        Services.GetService<IConversationTurnRunner>() ?? new NullConversationTurnRunner();

    private ConversationTurnRuntimeContext CaptureNyxRelayReplyToken(
        NyxRelayInboundActivity relayActivity,
        ChatActivity activity)
    {
        SweepExpiredNyxRelayReplyTokens();

        var outboundDelivery = activity.OutboundDelivery;
        var correlationId = NormalizeOptional(outboundDelivery?.CorrelationId) ??
                            NormalizeOptional(relayActivity.CorrelationId);
        var replyToken = NormalizeOptional(relayActivity.ReplyToken);
        var replyMessageId = NormalizeOptional(outboundDelivery?.ReplyMessageId);
        if (correlationId is null || replyToken is null || replyMessageId is null)
            return ConversationTurnRuntimeContext.Empty;

        var expiresAt = relayActivity.ReplyTokenExpiresAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(relayActivity.ReplyTokenExpiresAtUnixMs)
            : DateTimeOffset.UtcNow.AddMinutes(30);
        var tokenContext = new NyxRelayReplyTokenContext(
            correlationId,
            replyToken,
            replyMessageId,
            expiresAt);
        _nyxRelayReplyTokens[correlationId] = tokenContext;
        return new ConversationTurnRuntimeContext(tokenContext);
    }

    private ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity)
    {
        SweepExpiredNyxRelayReplyTokens();

        var normalizedCorrelationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                      NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return ConversationTurnRuntimeContext.Empty;

        if (!_nyxRelayReplyTokens.TryGetValue(normalizedCorrelationId, out var tokenContext))
            return ConversationTurnRuntimeContext.Empty;

        if (tokenContext.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _nyxRelayReplyTokens.Remove(normalizedCorrelationId);
            return ConversationTurnRuntimeContext.Empty;
        }

        return new ConversationTurnRuntimeContext(tokenContext);
    }

    private ConversationTurnRuntimeContext BuildNyxRelayRuntimeContextForReply(
        LlmReplyReadyEvent evt,
        ChatActivity? pendingActivity)
    {
        var activity = pendingActivity ?? evt.Activity;

        // Inbox-echoed credential is the authoritative source — it survives actor
        // deactivation between inbound capture and LLM reply ready, which the in-memory
        // dict cannot. Fall back to the dict only when the inbox didn't carry a token
        // (legacy in-flight messages from before this change deployed).
        var inlineToken = NormalizeOptional(evt.ReplyToken);
        if (inlineToken is not null)
        {
            var expiresAt = evt.ReplyTokenExpiresAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(evt.ReplyTokenExpiresAtUnixMs)
                : DateTimeOffset.UtcNow.AddMinutes(30);
            if (expiresAt > DateTimeOffset.UtcNow)
            {
                var correlationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                    NormalizeOptional(evt.CorrelationId) ??
                                    string.Empty;
                var replyMessageId = NormalizeOptional(activity?.OutboundDelivery?.ReplyMessageId) ?? string.Empty;
                return new ConversationTurnRuntimeContext(
                    new NyxRelayReplyTokenContext(correlationId, inlineToken, replyMessageId, expiresAt));
            }
        }

        return BuildNyxRelayRuntimeContext(evt.CorrelationId, activity);
    }

    private string DescribeReplyTokenSource(LlmReplyReadyEvent evt, ConversationTurnRuntimeContext runtimeContext)
    {
        if (runtimeContext.NyxRelayReplyToken is null)
            return "none";
        if (!string.IsNullOrWhiteSpace(evt.ReplyToken))
            return "inbox-echo";
        return "actor-runtime-dict";
    }

    private void SweepExpiredNyxRelayReplyTokens()
    {
        if (_nyxRelayReplyTokens.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var token in _nyxRelayReplyTokens.ToArray())
        {
            if (token.Value.ExpiresAtUtc <= now)
                _nyxRelayReplyTokens.Remove(token.Key);
        }
    }

    private void RemoveNyxRelayReplyToken(string? correlationId, ChatActivity? activity)
    {
        var normalizedCorrelationId = NormalizeOptional(activity?.OutboundDelivery?.CorrelationId) ??
                                      NormalizeOptional(correlationId);
        if (normalizedCorrelationId is not null)
        {
            _nyxRelayReplyTokens.Remove(normalizedCorrelationId);
            _nyxRelayStreamingStates.Remove(normalizedCorrelationId);
        }
    }

    private static OutboundDeliveryReceipt? ToOutboundDeliveryReceipt(OutboundDeliveryContext? outboundDelivery)
    {
        var replyMessageId = outboundDelivery?.ReplyMessageId;
        return string.IsNullOrWhiteSpace(replyMessageId)
            ? null
            : new OutboundDeliveryReceipt { ReplyMessageId = replyMessageId };
    }

    // ─── State transitions ───

    private static ConversationGAgentState ApplyTurnCompleted(
        ConversationGAgentState current,
        ConversationTurnCompletedEvent evt)
    {
        var next = current.Clone();
        if (!string.IsNullOrEmpty(evt.ProcessedActivityId))
        {
            AppendBounded(next.ProcessedMessageIds, evt.ProcessedActivityId, ProcessedIdsCap);
            // Successful inbound completion supersedes any pending retry entry.
            RemovePendingInboundTurn(next.PendingInboundTurns, evt.ProcessedActivityId);
        }
        if (!string.IsNullOrEmpty(evt.CausationCommandId))
        {
            AppendBounded(next.ProcessedCommandIds, evt.CausationCommandId, ProcessedIdsCap);
            RemovePendingLlmReplyRequest(next.PendingLlmReplyRequests, ExtractLlmReplyCorrelationId(evt.CausationCommandId));
        }
        if (evt.Conversation != null && next.Conversation == null)
        {
            next.Conversation = evt.Conversation.Clone();
        }
        next.LastUpdatedUnixMs = evt.CompletedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyInboundTurnRetryScheduled(
        ConversationGAgentState current,
        InboundTurnRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (string.IsNullOrEmpty(evt.ActivityId))
            return next;

        var pending = new PendingInboundTurn
        {
            ActivityId = evt.ActivityId,
            Activity = evt.Activity?.Clone(),
            RetryCount = evt.RetryCount,
            FirstFailedUnixMs = evt.FirstFailedUnixMs,
            NextRetryUnixMs = evt.NextRetryUnixMs,
        };
        UpsertPendingInboundTurn(next.PendingInboundTurns, pending);
        next.LastUpdatedUnixMs = evt.ScheduledAtUnixMs > 0 ? evt.ScheduledAtUnixMs : evt.NextRetryUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyLlmReplyRequested(
        ConversationGAgentState current,
        NeedsLlmReplyEvent evt)
    {
        var next = current.Clone();
        var activityId = evt.Activity?.Id;
        if (!string.IsNullOrWhiteSpace(activityId))
        {
            AppendBounded(next.ProcessedMessageIds, activityId, ProcessedIdsCap);
            // Acceptance into the LLM reply pipeline supersedes any pending inbound retry
            // entry for the same activity. Without this reap, a redelivery that takes the
            // LLM path would leave the stale pending entry in state, where it would be
            // re-scheduled on every activation and silently no-op against the dedup guard.
            RemovePendingInboundTurn(next.PendingInboundTurns, activityId);
        }

        if (evt.Activity?.Conversation != null && next.Conversation == null)
        {
            next.Conversation = evt.Activity.Conversation.Clone();
        }

        UpsertPendingLlmReplyRequest(next.PendingLlmReplyRequests, evt);
        next.LastUpdatedUnixMs = evt.RequestedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyContinueRejected(
        ConversationGAgentState current,
        ConversationContinueRejectedEvent evt)
    {
        var next = current.Clone();
        if (evt.Reason == RejectReason.DuplicateCommand && !string.IsNullOrEmpty(evt.CommandId))
        {
            // DuplicateCommand rejection is emitted *because* the command id is already processed.
            // No state change. Fall through and just stamp the timestamp.
        }
        else if (!string.IsNullOrEmpty(evt.CommandId))
        {
            AppendBounded(next.ProcessedCommandIds, evt.CommandId, ProcessedIdsCap);
        }
        next.LastUpdatedUnixMs = evt.RejectedAtUnixMs;
        return next;
    }

    private static ConversationGAgentState ApplyContinueFailed(
        ConversationGAgentState current,
        ConversationContinueFailedEvent evt)
    {
        var next = current.Clone();
        // Only terminal failures (NotRetryable oneof) consume the command id. `retry_after_ms`
        // failures must stay retriable — if we appended them here the next redispatch of the same
        // logical command id would come back as DuplicateCommand instead of executing.
        if (!string.IsNullOrEmpty(evt.CommandId)
            && evt.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
        {
            AppendBounded(next.ProcessedCommandIds, evt.CommandId, ProcessedIdsCap);
            RemovePendingLlmReplyRequest(next.PendingLlmReplyRequests, ExtractLlmReplyCorrelationId(evt.CommandId));
        }
        // Inbound terminal failures (e.g. retries exhausted) carry an empty CommandId and set
        // CorrelationId to the activity id; reap the matching pending retry entry so the set
        // does not leak.
        if (string.IsNullOrEmpty(evt.CommandId)
            && !string.IsNullOrEmpty(evt.CorrelationId)
            && evt.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
        {
            RemovePendingInboundTurn(next.PendingInboundTurns, evt.CorrelationId);
        }
        next.LastUpdatedUnixMs = evt.FailedAtUnixMs;
        return next;
    }

    private NeedsLlmReplyEvent? FindPendingLlmReplyRequest(string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return null;

        return State.PendingLlmReplyRequests.FirstOrDefault(request =>
            string.Equals(request.CorrelationId, normalizedCorrelationId, StringComparison.Ordinal));
    }

    private static void UpsertPendingLlmReplyRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsLlmReplyEvent> field,
        NeedsLlmReplyEvent request)
    {
        RemovePendingLlmReplyRequest(field, request.CorrelationId);
        field.Add(request.Clone());
    }

    private static void RemovePendingLlmReplyRequest(
        Google.Protobuf.Collections.RepeatedField<NeedsLlmReplyEvent> field,
        string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);
        if (normalizedCorrelationId is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].CorrelationId, normalizedCorrelationId, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private PendingInboundTurn? FindPendingInboundTurn(string? activityId)
    {
        var normalized = NormalizeOptional(activityId);
        if (normalized is null)
            return null;

        return State.PendingInboundTurns.FirstOrDefault(entry =>
            string.Equals(entry.ActivityId, normalized, StringComparison.Ordinal));
    }

    private static void UpsertPendingInboundTurn(
        Google.Protobuf.Collections.RepeatedField<PendingInboundTurn> field,
        PendingInboundTurn entry)
    {
        RemovePendingInboundTurn(field, entry.ActivityId);
        field.Add(entry.Clone());
    }

    private static void RemovePendingInboundTurn(
        Google.Protobuf.Collections.RepeatedField<PendingInboundTurn> field,
        string? activityId)
    {
        var normalized = NormalizeOptional(activityId);
        if (normalized is null)
            return;

        for (var i = field.Count - 1; i >= 0; i--)
        {
            if (string.Equals(field[i].ActivityId, normalized, StringComparison.Ordinal))
                field.RemoveAt(i);
        }
    }

    private static string? ExtractLlmReplyCorrelationId(string? commandId)
    {
        var normalizedCommandId = NormalizeOptional(commandId);
        if (normalizedCommandId is null ||
            !normalizedCommandId.StartsWith("llm:", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizeOptional(normalizedCommandId["llm:".Length..]);
    }

    private static void AppendBounded(
        Google.Protobuf.Collections.RepeatedField<string> field,
        string value,
        int cap)
    {
        field.Add(value);
        while (field.Count > cap)
            field.RemoveAt(0);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
