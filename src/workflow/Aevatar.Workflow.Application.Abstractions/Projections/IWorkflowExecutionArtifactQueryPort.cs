using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionArtifactQueryPort
{
    bool EnableActorQueryEndpoints { get; }

    Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default);
}
