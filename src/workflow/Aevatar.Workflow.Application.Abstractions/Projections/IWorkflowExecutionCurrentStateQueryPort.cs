using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionCurrentStateQueryPort
{
    bool WorkflowRunCurrentStateQueryEnabled { get; }

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: query callers requested an actor snapshot by raw actorId through actor-query naming.
    //   New principle: query callers request a workflow-run current-state readmodel by workflowRunId.
    Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateAsync(
        string workflowRunId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowRunCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<WorkflowActorProjectionState?> GetWorkflowRunProjectionStateAsync(
        string workflowRunId,
        CancellationToken ct = default);
}
