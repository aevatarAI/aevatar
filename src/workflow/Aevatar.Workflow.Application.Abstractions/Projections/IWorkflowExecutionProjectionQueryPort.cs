using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionQueryPort
{
    bool EnableActorQueryEndpoints { get; }

    Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorRelationItem>> GetActorRelationsAsync(
        string actorId,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowActorRelationSubgraph> GetActorRelationSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default);

    Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default);
}
