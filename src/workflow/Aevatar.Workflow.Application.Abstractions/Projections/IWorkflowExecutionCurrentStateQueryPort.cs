using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public sealed class WorkflowActorCurrentStateListQuery
{
    public int Take { get; init; } = 200;

    public WorkflowSagaStatus? SagaStatus { get; init; }

    public string ScopeId { get; init; } = string.Empty;

    public IReadOnlyList<string> DefinitionActorIds { get; init; } = [];
}

public interface IWorkflowExecutionCurrentStateQueryPort
{
    bool WorkflowActorCurrentStateQueryEnabled { get; }

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: query callers requested an actor snapshot by raw actorId through actor-query naming.
    //   New principle: query callers request a workflow actor current-state readmodel by actorId.
    Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default);

    Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default);
}
