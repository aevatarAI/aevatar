using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionQueryService
    : ProjectionQueryPortServiceBase<WorkflowActorSnapshot, WorkflowActorTimelineItem, WorkflowActorGraphEdge, WorkflowActorGraphSubgraph>,
      IWorkflowExecutionProjectionQueryPort
{
    private readonly IWorkflowProjectionQueryReader _queryReader;
    private readonly bool _enableActorQueryEndpoints;

    public WorkflowExecutionProjectionQueryService(
        WorkflowExecutionProjectionOptions options,
        IWorkflowProjectionQueryReader queryReader)
        : base(() => options.Enabled)
    {
        _queryReader = queryReader;
        _enableActorQueryEndpoints = options.Enabled && options.EnableActorQueryEndpoints;
    }

    public bool EnableActorQueryEndpoints => _enableActorQueryEndpoints;

    public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default) =>
        GetSnapshotAsync(actorId, ct);

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default) =>
        ListSnapshotsAsync(take, ct);

    public async Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return null;

        return await _queryReader.GetActorProjectionStateAsync(actorId, ct);
    }

    public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default) =>
        ListTimelineAsync(actorId, take, ct);

    public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default) =>
        GetGraphEdgesInternalAsync(actorId, take, options, ct);

    public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default) =>
        GetGraphSubgraphInternalAsync(actorId, depth, take, options, ct);

    public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
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

    protected override Task<IReadOnlyList<WorkflowActorGraphEdge>> ReadGraphEdgesCoreAsync(
        string entityId,
        int take,
        CancellationToken ct)
        => _queryReader.GetActorGraphEdgesAsync(entityId, take, options: null, ct);

    protected override Task<WorkflowActorGraphSubgraph> ReadGraphSubgraphCoreAsync(
        string entityId,
        int depth,
        int take,
        CancellationToken ct)
        => _queryReader.GetActorGraphSubgraphAsync(entityId, depth, take, options: null, ct);

    protected override WorkflowActorGraphSubgraph CreateEmptyGraphSubgraph(string entityId)
    {
        return new WorkflowActorGraphSubgraph
        {
            RootNodeId = entityId ?? string.Empty,
        };
    }

    private async Task<IReadOnlyList<WorkflowActorGraphEdge>> GetGraphEdgesInternalAsync(
        string actorId,
        int take,
        WorkflowActorGraphQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return [];

        return await _queryReader.GetActorGraphEdgesAsync(actorId, take, options, ct);
    }

    private async Task<WorkflowActorGraphSubgraph> GetGraphSubgraphInternalAsync(
        string actorId,
        int depth,
        int take,
        WorkflowActorGraphQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return CreateEmptyGraphSubgraph(actorId);

        return await _queryReader.GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);
    }

    private async Task<WorkflowActorGraphEnrichedSnapshot?> GetGraphEnrichedInternalAsync(
        string actorId,
        int depth,
        int take,
        WorkflowActorGraphQueryOptions? options,
        CancellationToken ct)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(actorId))
            return null;

        return await _queryReader.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, options, ct);
    }
}
