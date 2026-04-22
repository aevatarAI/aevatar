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
    /// <summary>
    /// Sliding window cap on retained processed ids. Keeps state size bounded while still
    /// catching typical redelivery windows (seconds to minutes).
    /// </summary>
    public const int ProcessedIdsCap = 10000;

    /// <inheritdoc />
    protected override ConversationGAgentState TransitionState(ConversationGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConversationTurnCompletedEvent>(ApplyTurnCompleted)
            .On<ConversationContinueRejectedEvent>(ApplyContinueRejected)
            .On<ConversationContinueFailedEvent>(ApplyContinueFailed)
            .OrCurrent();

    /// <summary>
    /// Authoritative inbound admission: dedup + run bot turn + commit atomically.
    /// </summary>
    [EventHandler]
    public async Task HandleInboundActivityAsync(ChatActivity activity)
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
        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
        }
        if (evt.Conversation != null && next.Conversation == null)
        {
            next.Conversation = evt.Conversation.Clone();
        }
        next.LastUpdatedUnixMs = evt.CompletedAtUnixMs;
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
        }
        next.LastUpdatedUnixMs = evt.FailedAtUnixMs;
        return next;
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
}
