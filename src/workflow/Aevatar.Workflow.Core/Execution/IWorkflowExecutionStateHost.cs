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

    WorkflowExecutionRuntimeContext RuntimeContext { get; }

    WorkflowRunExecutionContextState ExecutionContextSnapshot { get; }

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
        CancellationToken ct = default);

    Task<WorkflowCompensationTransitionResult> TryStartCompensationAsync(
        WorkflowCompletedEvent terminalFailure,
        StepCompletedEvent? terminalStep,
        CancellationToken ct = default) =>
        TryStartCompensationAsync(terminalFailure, ct);

    Task RecordCompensableStepDispatchAsync(
        CompensableStepDispatchedEvent evt,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct = default);
}

internal interface IWorkflowExecutionStateHostAccessor
{
    IWorkflowExecutionStateHost StateHost { get; }
}
