using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionQueryService
    : ProjectionQueryPortServiceBase<WorkflowActorSnapshot, WorkflowActorTimelineItem, WorkflowActorRelationItem, WorkflowActorRelationSubgraph>,
      IWorkflowExecutionProjectionQueryPort
{
    private readonly IWorkflowProjectionQueryReader _queryReader;

    public WorkflowExecutionProjectionQueryService(
        WorkflowExecutionProjectionOptions options,
        IWorkflowProjectionQueryReader queryReader)
        : base(() => options.Enabled && options.EnableActorQueryEndpoints)
    {
        _queryReader = queryReader;
    }

    public bool EnableActorQueryEndpoints => QueryEnabledCore;

    public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default) =>
        GetSnapshotAsync(actorId, ct);

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default) =>
        ListSnapshotsAsync(take, ct);

    public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default) =>
        ListTimelineAsync(actorId, take, ct);

    public Task<IReadOnlyList<WorkflowActorRelationItem>> GetActorRelationsAsync(
        string actorId,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default) =>
        GetRelationsInternalAsync(actorId, take, options, ct);

    public Task<WorkflowActorRelationSubgraph> GetActorRelationSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default) =>
        GetRelationSubgraphInternalAsync(actorId, depth, take, options, ct);

    public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorRelationQueryOptions? options = null,
        CancellationToken ct = default) =>
        GetGraphEnrichedInternalAsync(actorId, depth, take, options, ct);

    protected override Task<WorkflowActorSnapshot?> ReadSnapshotCoreAsync(
        string entityId,
        CancellationToken ct)
        => _queryReader.GetActorSnapshotAsync(entityId, ct);

    protected override Task<IReadOnlyList<WorkflowActorSnapshot>> ReadSnapshotsCoreAsync(
        int take,
        CancellationToken ct)
        => _queryReader.ListActorSnapshotsAsync(take, ct);

    protected override Task<IReadOnlyList<WorkflowActorTimelineItem>> ReadTimelineCoreAsync(
        string entityId,
        int take,
        CancellationToken ct)
        => _queryReader.ListActorTimelineAsync(entityId, take, ct);

    protected override Task<IReadOnlyList<WorkflowActorRelationItem>> ReadRelationsCoreAsync(
        string entityId,
        int take,
        CancellationToken ct)
        => _queryReader.GetActorRelationsAsync(entityId, take, options: null, ct);

    protected override Task<WorkflowActorRelationSubgraph> ReadRelationSubgraphCoreAsync(
        string entityId,
        int depth,
        int take,
        CancellationToken ct)
        => _queryReader.GetActorRelationSubgraphAsync(entityId, depth, take, options: null, ct);

    protected override WorkflowActorRelationSubgraph CreateEmptyRelationSubgraph(string entityId)
    {
        return new WorkflowActorRelationSubgraph
        {
            RootNodeId = entityId ?? string.Empty,
        };
    }

    private async Task<IReadOnlyList<WorkflowActorRelationItem>> GetRelationsInternalAsync(
        string actorId,
        int take,
        WorkflowActorRelationQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _queryReader.GetActorRelationsAsync(actorId, take, options, ct);
    }

    private async Task<WorkflowActorRelationSubgraph> GetRelationSubgraphInternalAsync(
        string actorId,
        int depth,
        int take,
        WorkflowActorRelationQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return CreateEmptyRelationSubgraph(actorId);

        return await _queryReader.GetActorRelationSubgraphAsync(actorId, depth, take, options, ct);
    }

    private async Task<WorkflowActorGraphEnrichedSnapshot?> GetGraphEnrichedInternalAsync(
        string actorId,
        int depth,
        int take,
        WorkflowActorRelationQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return null;

        return await _queryReader.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, options, ct);
    }
}
