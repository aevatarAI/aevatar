using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

internal enum WorkflowCompensationTransitionStatus
{
    Started,
    AlreadyCompensating,
    NoCompensableLedger,
    AdvancedAndRequestedNext,
    CompletedAll,
    RejectedStaleOrDuplicate,
    CompensationDeadLettered,
}

internal readonly record struct WorkflowCompensationTransitionResult(
    WorkflowCompensationTransitionStatus Status,
    string NextCompensationStepId,   // empty when no next request should be sent
    string FailedStepId,
    string IdempotencyKey,
    string CapturedOutput,
    string ExecutionId);

// Refactor (iter115/cluster-3):
//   Old pattern: WorkflowRunGAgent exposed only a process-local runtime context,
//                so durable control/security facts could not survive replay.
//   New principle: execution facades keep their names but read/write typed
//                  WorkflowRunState execution context owned by the actor.
internal interface IWorkflowExecutionStateHost
{
    string RunId { get; }

    string ScopeId => string.Empty;

    string ScheduleId => string.Empty;

    WorkflowExecutionRuntimeContext RuntimeContext { get; }

    WorkflowRunExecutionContextState ExecutionContextSnapshot { get; }

    /// <summary>
    /// Committed call-site admission proof this Run received at bind time. The Run actor owns
    /// this copy; runtime never re-reads the Definition actor, a read model, or an event store.
    /// </summary>
    WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlanSnapshot => new();

    Task UpdateExecutionContextAsync(
        WorkflowRunExecutionContextDelta delta,
        CancellationToken ct = default);

    Task ClearExecutionContextAsync(CancellationToken ct = default);

    Any? GetExecutionState(string scopeKey);

    IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates();

    Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default);

    Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default);

    Task<WorkflowCompensationTransitionResult> TryStartCompensationAsync(
        WorkflowCompletedEvent terminalFailure,
        StepCompletedEvent? terminalStep,
        CancellationToken ct = default);

    Task RecordCompensableStepDispatchAsync(
        CompensableStepDispatchedEvent evt,
        CancellationToken ct = default);

    Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct = default);

    Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
        string runId,
        string error,
        CancellationToken ct = default);
}

internal interface IWorkflowExecutionStateHostAccessor
{
    IWorkflowExecutionStateHost StateHost { get; }
}
