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
    private readonly Dictionary<string, NyxRelayReplyTokenContext> _nyxRelayReplyTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Sliding window cap on retained processed ids. Keeps state size bounded while still
    /// catching typical redelivery windows (seconds to minutes).
    /// </summary>
    public const int ProcessedIdsCap = 10000;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await SchedulePendingLlmReplyDispatchesAsync(ct);
    }

    /// <inheritdoc />
    protected override ConversationGAgentState TransitionState(ConversationGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationTurnCompletedEvent>(ApplyTurnCompleted)
            .On<NeedsLlmReplyEvent>(ApplyLlmReplyRequested)
            .On<ConversationContinueRejectedEvent>(ApplyContinueRejected)
            .On<ConversationContinueFailedEvent>(ApplyContinueFailed)
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

        var runtimeContext = BuildNyxRelayRuntimeContextForReply(evt, pendingRequest?.Activity);
        Logger.LogInformation(
            "Received LLM reply ready: correlation={CorrelationId} terminal={TerminalState} replyTokenSource={Source}",
            evt.CorrelationId,
            evt.TerminalState,
            DescribeReplyTokenSource(evt, runtimeContext));

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
        var correlationId = NormalizeOptional(relayActivity.CorrelationId) ??
                            NormalizeOptional(outboundDelivery?.CorrelationId);
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
        var normalizedCorrelationId = NormalizeOptional(correlationId) ??
                                      NormalizeOptional(activity?.OutboundDelivery?.CorrelationId);
        if (normalizedCorrelationId is not null)
            _nyxRelayReplyTokens.Remove(normalizedCorrelationId);
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

    private static ConversationGAgentState ApplyLlmReplyRequested(
        ConversationGAgentState current,
        NeedsLlmReplyEvent evt)
    {
        var next = current.Clone();
        var activityId = evt.Activity?.Id;
        if (!string.IsNullOrWhiteSpace(activityId))
        {
            AppendBounded(next.ProcessedMessageIds, activityId, ProcessedIdsCap);
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
