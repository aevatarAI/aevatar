using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ToolCallModule
{
    private void RecordToolCallTiming(WorkflowToolCallAttemptTimingObservation observation) =>
        WorkflowToolCallTelemetry.Record(_logger, observation);

    private static WorkflowToolCallAttemptTimingObservation CreateToolCallTimingObservation(
        IWorkflowExecutionContext ctx,
        string runId,
        string stepId,
        string callId,
        string executionId,
        string continuationId,
        int attempt,
        WorkflowToolCallAttemptWaterline waterline,
        string dispatchId = "") =>
        new()
        {
            ScopeId = NormalizeRequired(ctx.ScopeId),
            RunId = NormalizeRequired(runId),
            StepId = NormalizeRequired(stepId),
            CallId = NormalizeRequired(callId),
            ExecutionId = NormalizeRequired(executionId),
            ContinuationId = NormalizeRequired(continuationId),
            Attempt = Math.Max(0, attempt),
            DispatchId = NormalizeRequired(dispatchId),
            Waterline = waterline,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(ctx.UtcNow),
        };

    private static WorkflowToolCallProviderDisposition ResolveProviderDisposition(
        WorkflowToolExecutionResult result)
    {
        if (result.PendingOperation != null)
            return WorkflowToolCallProviderDisposition.PendingOperation;
        if (result.PendingApproval != null)
            return WorkflowToolCallProviderDisposition.ApprovalRequired;
        if (result.Failure != null)
            return WorkflowToolCallProviderDisposition.Failed;
        return WorkflowToolCallProviderDisposition.Succeeded;
    }

    private WorkflowToolCallProviderTiming RecordProviderReturned(
        IWorkflowExecutionContext ctx,
        WorkflowToolExecutionRequest request,
        int attempt,
        string continuationId,
        string dispatchId,
        long providerStartedAtTimestamp,
        DateTimeOffset providerStartedAtUtc,
        WorkflowToolCallProviderDisposition disposition)
    {
        var providerReturnedAtUtc = ctx.UtcNow;
        var externalElapsedMs = ElapsedMilliseconds(ctx, providerStartedAtTimestamp);
        var timing = new WorkflowToolCallProviderTiming
        {
            DispatchId = dispatchId,
            DispatchStartedAtUtc = Timestamp.FromDateTimeOffset(providerStartedAtUtc),
            ProviderReturnedAtUtc = Timestamp.FromDateTimeOffset(providerReturnedAtUtc),
            ExternalExecutionElapsedMs = externalElapsedMs,
            Disposition = disposition,
        };
        var observation = CreateToolCallTimingObservation(
            ctx,
            request.RunId,
            request.StepId,
            request.CallId,
            request.ExecutionId,
            continuationId,
            attempt,
            WorkflowToolCallAttemptWaterline.ProviderReturned,
            dispatchId);
        observation.ProviderDisposition = disposition;
        observation.ExternalExecutionElapsedMs = externalElapsedMs;
        RecordToolCallTiming(observation);
        return timing;
    }

    private static long ElapsedMilliseconds(
        IWorkflowExecutionContext ctx,
        long startedAtTimestamp) =>
        Math.Max(0, (long)Math.Ceiling(ctx.GetElapsedTime(startedAtTimestamp).TotalMilliseconds));

    private void RecordCompletionReconciliation(
        IWorkflowExecutionContext ctx,
        WorkflowToolCallAttemptCompletedEvent completed,
        long reconciliationStartedAtTimestamp,
        WorkflowToolCallReconciliationDisposition disposition)
    {
        var observation = CreateToolCallTimingObservation(
            ctx,
            completed.RunId,
            completed.StepId,
            completed.CallId,
            completed.ExecutionId,
            completed.ContinuationId,
            completed.Attempt,
            WorkflowToolCallAttemptWaterline.ActorReconciliationCompleted,
            completed.ProviderTiming?.DispatchId ?? string.Empty);
        observation.ReconciliationDisposition = disposition;
        observation.ActorReconciliationElapsedMs = ElapsedMilliseconds(ctx, reconciliationStartedAtTimestamp);
        RecordToolCallTiming(observation);
    }

    private void RecordTimeoutReconciliation(
        IWorkflowExecutionContext ctx,
        WorkflowToolCallTimeoutFiredEvent timeout,
        long reconciliationStartedAtTimestamp,
        WorkflowToolCallReconciliationDisposition disposition,
        int attemptOverride = -1)
    {
        var observation = CreateToolCallTimingObservation(
            ctx,
            timeout.RunId,
            timeout.StepId,
            timeout.CallId,
            timeout.ExecutionId,
            timeout.ContinuationId,
            attemptOverride >= 0 ? attemptOverride : timeout.Attempt,
            WorkflowToolCallAttemptWaterline.ActorReconciliationCompleted);
        observation.ReconciliationDisposition = disposition;
        observation.ActorReconciliationElapsedMs = ElapsedMilliseconds(ctx, reconciliationStartedAtTimestamp);
        RecordToolCallTiming(observation);
    }

    /// <summary>
    /// Records <c>actor_reconciliation_completed</c> for an attempt that the actor settled
    /// without a provider completion signal (pre-dispatch failure, deadline before dispatch,
    /// recovery terminalization, stale/untrusted wakeups). Identity comes from the persisted
    /// pending execution; no dispatch id exists at this layer.
    /// </summary>
    private void RecordPendingReconciliation(
        IWorkflowExecutionContext ctx,
        PendingToolCallExecutionState pending,
        long reconciliationStartedAtTimestamp,
        WorkflowToolCallReconciliationDisposition disposition)
    {
        var observation = CreateToolCallTimingObservation(
            ctx,
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            pending.ContinuationId,
            pending.Attempt,
            WorkflowToolCallAttemptWaterline.ActorReconciliationCompleted);
        observation.ReconciliationDisposition = disposition;
        observation.ActorReconciliationElapsedMs = ElapsedMilliseconds(ctx, reconciliationStartedAtTimestamp);
        RecordToolCallTiming(observation);
    }

    private void RecordPendingStatePersisted(
        IWorkflowExecutionContext ctx,
        PendingToolCallExecutionState pending,
        long preparationStartedAtTimestamp)
    {
        var observation = CreateToolCallTimingObservation(
            ctx,
            pending.RunId,
            pending.StepId,
            pending.CallId,
            pending.ExecutionId,
            pending.ContinuationId,
            pending.Attempt,
            WorkflowToolCallAttemptWaterline.PendingStatePersisted);
        observation.PreparationElapsedMs = ElapsedMilliseconds(ctx, preparationStartedAtTimestamp);
        RecordToolCallTiming(observation);
    }

    private static WorkflowToolCallReconciliationDisposition ResolveDeadlineReconciliationDisposition(
        bool outcomeMayBeUnknown) =>
        outcomeMayBeUnknown
            ? WorkflowToolCallReconciliationDisposition.TimeoutOutcomeUnknown
            : WorkflowToolCallReconciliationDisposition.RetryDeadlineExceeded;

    private void RecordTimeoutAccepted(
        IWorkflowExecutionContext ctx,
        WorkflowToolCallTimeoutFiredEvent timeout,
        int attempt,
        WorkflowToolCallReconciliationDisposition disposition)
    {
        var observation = CreateToolCallTimingObservation(
            ctx,
            timeout.RunId,
            timeout.StepId,
            timeout.CallId,
            timeout.ExecutionId,
            timeout.ContinuationId,
            attempt,
            WorkflowToolCallAttemptWaterline.TimeoutSignalAccepted);
        observation.ReconciliationDisposition = disposition;
        RecordToolCallTiming(observation);
    }

    private static WorkflowToolCallReconciliationDisposition ResolveCompletionReconciliationDisposition(
        WorkflowToolCallAttemptCompletedEvent completed) =>
        completed.OutcomeCase switch
        {
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Success =>
                WorkflowToolCallReconciliationDisposition.Succeeded,
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.Failure =>
                WorkflowToolCallReconciliationDisposition.ProviderFailed,
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.ApprovalRequired =>
                WorkflowToolCallReconciliationDisposition.ApprovalSuspended,
            WorkflowToolCallAttemptCompletedEvent.OutcomeOneofCase.PendingOperation =>
                WorkflowToolCallReconciliationDisposition.PendingOperation,
            _ => WorkflowToolCallReconciliationDisposition.ProviderFailed,
        };
}
