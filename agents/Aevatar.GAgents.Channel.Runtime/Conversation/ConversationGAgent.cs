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
    private static readonly TimeSpan InitialDeferredLlmDispatchDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeferredLlmDispatchRetryDelay = TimeSpan.FromSeconds(5);
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
    public Task HandleNyxRelayInboundActivityAsync(NyxRelayInboundActivity relayActivity)
    {
        ArgumentNullException.ThrowIfNull(relayActivity);

        var activity = relayActivity.Activity?.Clone() ?? new ChatActivity();
        var runtimeContext = CaptureNyxRelayReplyToken(relayActivity, activity);
        return HandleInboundActivityCoreAsync(activity, runtimeContext);
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
            return;
        }

        var runner = ResolveRunner();
        var result = await runner.RunInboundAsync(activity, runtimeContext, CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result.LlmReplyRequest is not null)
        {
            await PersistDomainEventAsync(result.LlmReplyRequest);
            await ScheduleDeferredLlmReplyDispatchAsync(
                result.LlmReplyRequest,
                InitialDeferredLlmDispatchDelay,
                CancellationToken.None);
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

        var inbox = Services.GetService<IChannelLlmReplyInbox>();
        if (inbox is null)
        {
            Logger.LogWarning(
                "Deferred LLM reply inbox not registered; rescheduling dispatch: correlation={CorrelationId}",
                evt.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(
                pendingRequest,
                DeferredLlmDispatchRetryDelay,
                CancellationToken.None);
            return;
        }

        try
        {
            await inbox.EnqueueAsync(pendingRequest.Clone(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to enqueue deferred LLM reply request; rescheduling dispatch: correlation={CorrelationId}",
                evt.CorrelationId);
            await ScheduleDeferredLlmReplyDispatchAsync(
                pendingRequest,
                DeferredLlmDispatchRetryDelay,
                CancellationToken.None);
        }
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

        var runner = ResolveRunner();
        var result = await runner.RunLlmReplyAsync(
            evt,
            BuildNyxRelayRuntimeContext(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity),
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
        if (failed.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable)
        {
            RemoveNyxRelayReplyToken(evt.CorrelationId, pendingRequest?.Activity ?? evt.Activity);
        }
        if (failed.RetryPolicyCase != ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable &&
            pendingRequest is not null)
        {
            var retryAfter = failed.RetryAfterMs > 0
                ? TimeSpan.FromMilliseconds(failed.RetryAfterMs)
                : DeferredLlmDispatchRetryDelay;
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

    private async Task ScheduleDeferredLlmReplyDispatchAsync(
        NeedsLlmReplyEvent request,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        await ScheduleSelfDurableTimeoutAsync(
            BuildDeferredLlmReplyCallbackId(request.CorrelationId),
            dueTime <= TimeSpan.Zero ? InitialDeferredLlmDispatchDelay : dueTime,
            new DeferredLlmReplyDispatchRequestedEvent
            {
                CorrelationId = request.CorrelationId,
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ct: ct);
    }

    private async Task SchedulePendingLlmReplyDispatchesAsync(CancellationToken ct)
    {
        foreach (var request in State.PendingLlmReplyRequests)
        {
            await ScheduleDeferredLlmReplyDispatchAsync(
                request,
                InitialDeferredLlmDispatchDelay,
                ct);
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
        var normalizedCorrelationId = NormalizeOptional(correlationId) ??
                                      NormalizeOptional(activity?.OutboundDelivery?.CorrelationId);
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
