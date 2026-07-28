using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.WorkOrder;

public sealed partial class WorkOrderGAgent
{
    protected override WorkOrderState TransitionState(WorkOrderState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkOrderCreatedEvent>(ApplyCreated)
            .On<WorkOrderReadyEvent>(ApplyReady)
            .On<WorkOrderReassignedEvent>(ApplyReassigned)
            .On<WorkOrderDispatchRequestedEvent>(ApplyDispatchRequested)
            .On<WorkOrderExecutionRetryScheduledEvent>(ApplyExecutionRetryScheduled)
            .On<WorkOrderRunAcceptedEvent>(ApplyRunAccepted)
            .On<WorkOrderRunStartedEvent>(ApplyRunStarted)
            .On<WorkOrderDispatchFailedEvent>(ApplyDispatchFailed)
            .On<WorkOrderCancelledEvent>(ApplyCancelled)
            .On<WorkOrderTimedOutEvent>(ApplyTimedOut)
            .On<WorkOrderRunOutcomeObservedEvent>(ApplyRunOutcome)
            .On<WorkOrderLateRunOutcomeObservedEvent>(ApplyLateRunOutcome)
            .OrCurrent();

    private static WorkOrderState ApplyCreated(WorkOrderState _, WorkOrderCreatedEvent evt)
    {
        var request = evt.Request ?? throw new InvalidOperationException("created event request is required.");
        var createdAt = evt.CreatedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return new WorkOrderState
        {
            WorkOrderId = request.WorkOrderId,
            DedupKey = request.DedupKey,
            ScopeId = request.ScopeId,
            TeamId = request.TeamId,
            Requester = request.Requester?.Clone(),
            MemberId = request.MemberId,
            PublishedServiceId = request.PublishedServiceId,
            WorkflowId = request.WorkflowId,
            ServiceRevisionId = request.ServiceRevisionId,
            ImplementationKind = request.ImplementationKind,
            EndpointId = request.EndpointId,
            Intent = request.Intent,
            Input = request.Input?.Clone(),
            LifecycleStatus = WorkOrderLifecycleStatus.Accepted,
            LifecycleVersion = 1,
            CreatedAtUtc = createdAt.Clone(),
            UpdatedAtUtc = createdAt.Clone(),
            TimeoutAtUtc = request.TimeoutAtUtc?.Clone(),
            CreationRequest = request.Clone(),
        };
    }

    private static WorkOrderState ApplyReady(WorkOrderState current, WorkOrderReadyEvent evt)
    {
        var next = current.Clone();
        next.LifecycleStatus = WorkOrderLifecycleStatus.Ready;
        Advance(next, current, evt.ReadyAtUtc);
        return next;
    }

    private static WorkOrderState ApplyReassigned(WorkOrderState current, WorkOrderReassignedEvent evt)
    {
        var next = current.Clone();
        next.MemberId = evt.MemberId;
        next.PublishedServiceId = evt.PublishedServiceId;
        next.WorkflowId = evt.WorkflowId;
        next.ServiceRevisionId = evt.ServiceRevisionId;
        next.ImplementationKind = evt.ImplementationKind;
        Advance(next, current, evt.ReassignedAtUtc);
        return next;
    }

    private static WorkOrderState ApplyDispatchRequested(WorkOrderState current, WorkOrderDispatchRequestedEvent evt)
    {
        var next = current.Clone();
        next.DispatchCommandId = evt.DispatchCommandId;
        next.RequestedRunId = evt.RequestedRunId;
        next.TerminalDeliveryId = evt.TerminalDeliveryId;
        next.LifecycleStatus = WorkOrderLifecycleStatus.DispatchPending;
        Advance(next, current, evt.RequestedAtUtc);
        return next;
    }

    private static WorkOrderState ApplyExecutionRetryScheduled(
        WorkOrderState current,
        WorkOrderExecutionRetryScheduledEvent evt)
    {
        if (current.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending ||
            !string.Equals(current.WorkOrderId, evt.WorkOrderId, StringComparison.Ordinal) ||
            !string.Equals(current.DispatchCommandId, evt.DispatchCommandId, StringComparison.Ordinal) ||
            !string.Equals(current.RequestedRunId, evt.RequestedRunId, StringComparison.Ordinal) ||
            evt.Attempt <= current.ExecutionRetryAttempt)
        {
            return current;
        }

        var next = current.Clone();
        next.ExecutionRetryAttempt = evt.Attempt;
        next.ExecutionRetryCallbackId = evt.CallbackId;
        next.ExecutionRetryAtUtc = evt.RetryAtUtc?.Clone();
        return next;
    }

    private static WorkOrderState ApplyRunAccepted(WorkOrderState current, WorkOrderRunAcceptedEvent evt)
    {
        var next = current.Clone();
        var accepted = evt.Accepted ?? new WorkOrderExecutionAccepted();
        next.Run = new WorkOrderRunLink
        {
            RunId = accepted.RunId,
            RunActorId = accepted.RunActorId,
            CommandId = accepted.CommandId,
            CorrelationId = accepted.CorrelationId,
            RevisionId = accepted.RevisionId,
            DeploymentId = accepted.DeploymentId,
            AcceptedAtUtc = accepted.AcceptedAtUtc?.Clone(),
        };
        ClearExecutionRetry(next);
        Advance(next, current, accepted.AcceptedAtUtc);
        return next;
    }

    private static WorkOrderState ApplyRunStarted(WorkOrderState current, WorkOrderRunStartedEvent evt)
    {
        var next = current.Clone();
        if (current.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending)
            next.LifecycleStatus = WorkOrderLifecycleStatus.Running;
        Advance(next, current, evt.StartedAtUtc);
        return next;
    }

    private static WorkOrderState ApplyDispatchFailed(WorkOrderState current, WorkOrderDispatchFailedEvent evt)
    {
        var next = current.Clone();
        next.Failure = evt.Failure?.Clone();
        next.TerminalReason = evt.Failure?.Message ?? string.Empty;
        next.LifecycleStatus = WorkOrderLifecycleStatus.Failed;
        ClearExecutionRetry(next);
        Advance(next, current, evt.FailedAtUtc);
        return next;
    }

    private static WorkOrderState ApplyCancelled(WorkOrderState current, WorkOrderCancelledEvent evt)
    {
        var next = current.Clone();
        next.LifecycleStatus = WorkOrderLifecycleStatus.Cancelled;
        next.TerminalReason = evt.Reason;
        ClearExecutionRetry(next);
        Advance(next, current, evt.CancelledAtUtc);
        return next;
    }

    private static WorkOrderState ApplyTimedOut(WorkOrderState current, WorkOrderTimedOutEvent evt)
    {
        var next = current.Clone();
        next.LifecycleStatus = WorkOrderLifecycleStatus.TimedOut;
        next.TerminalReason = evt.Reason;
        ClearExecutionRetry(next);
        Advance(next, current, evt.TimedOutAtUtc);
        return next;
    }

    private static WorkOrderState ApplyRunOutcome(
        WorkOrderState current,
        WorkOrderRunOutcomeObservedEvent evt)
    {
        var next = current.Clone();
        next.LifecycleStatus = evt.LifecycleStatus;
        next.RunOutcome = evt.Outcome?.Clone();
        ClearExecutionRetry(next);
        Advance(next, current, evt.Outcome?.TerminalAtUtc);
        return next;
    }

    private static WorkOrderState ApplyLateRunOutcome(
        WorkOrderState current,
        WorkOrderLateRunOutcomeObservedEvent evt)
    {
        var next = current.Clone();
        next.LateRunOutcome = evt.Outcome?.Clone();
        ClearExecutionRetry(next);
        Advance(next, current, evt.Outcome?.TerminalAtUtc);
        return next;
    }

    private static void Advance(WorkOrderState next, WorkOrderState current, Timestamp? updatedAt)
    {
        next.LifecycleVersion = current.LifecycleVersion + 1;
        var candidate = updatedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        next.UpdatedAtUtc = current.UpdatedAtUtc != null && IsAfter(current.UpdatedAtUtc, candidate)
            ? current.UpdatedAtUtc.Clone()
            : candidate;
    }

    private static bool IsAfter(Timestamp left, Timestamp right) =>
        left.Seconds > right.Seconds ||
        left.Seconds == right.Seconds && left.Nanos > right.Nanos;

    private static void ClearExecutionRetry(WorkOrderState state)
    {
        state.ExecutionRetryAttempt = 0;
        state.ExecutionRetryCallbackId = string.Empty;
        state.ExecutionRetryAtUtc = null;
    }
}
