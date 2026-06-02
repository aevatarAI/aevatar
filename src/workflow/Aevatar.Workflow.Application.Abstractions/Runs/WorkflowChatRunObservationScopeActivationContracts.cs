namespace Aevatar.Workflow.Application.Abstractions.Runs;

// Refactor (issue-1687):
//   Old pattern: workflow draft-run command paths treated session activation as an application-owned pre-dispatch step.
//   New principle: this port only prepares the observation scope lease required for one interaction attempt.
//   It is not query priming and does not guarantee read-model freshness.
public sealed record WorkflowChatRunObservationScopeLeasePreparation(
    string ActorId,
    string CommandId);

public interface IWorkflowChatRunObservationScopeLeasePreparationPort
{
    Task<WorkflowChatRunObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string commandId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        WorkflowChatRunObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}
