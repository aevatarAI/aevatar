using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionQueryReader : IWorkflowProjectionQueryReader
{
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionRelationStore _relationStore;
    private readonly WorkflowExecutionReadModelMapper _mapper;

    public WorkflowProjectionQueryReader(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        WorkflowExecutionReadModelMapper mapper,
        IProjectionRelationStore? relationStore = null)
    {
        _store = store;
        _mapper = mapper;
        _relationStore = relationStore ?? NoopProjectionRelationStore.Instance;
    }

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var report = await _store.GetAsync(actorId, ct);
        return report == null ? null : _mapper.ToActorSnapshot(report);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var reports = await _store.ListAsync(boundedTake, ct);
        return reports
            .Select(_mapper.ToActorSnapshot)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var report = await _store.GetAsync(actorId, ct);
        if (report == null)
            return [];

        return report.Timeline
            .OrderByDescending(x => x.Timestamp)
            .Take(boundedTake)
            .Select(_mapper.ToActorTimelineItem)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowActorRelationItem>> GetActorRelationsAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        var actorIdValue = actorId?.Trim() ?? "";
        if (actorIdValue.Length == 0)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var edges = await _relationStore.GetNeighborsAsync(
            new ProjectionRelationQuery
            {
                Scope = WorkflowExecutionRelationConstants.Scope,
                RootNodeId = actorIdValue,
                Direction = ProjectionRelationDirection.Both,
                Take = boundedTake,
            },
            ct);
        return edges.Select(_mapper.ToActorRelationItem).ToList();
    }

    public async Task<WorkflowActorRelationSubgraph> GetActorRelationSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        CancellationToken ct = default)
    {
        var actorIdValue = actorId?.Trim() ?? "";
        if (actorIdValue.Length == 0)
            return new WorkflowActorRelationSubgraph();

        var boundedDepth = Math.Clamp(depth, 1, 8);
        var boundedTake = Math.Clamp(take, 1, 2000);
        var subgraph = await _relationStore.GetSubgraphAsync(
            new ProjectionRelationQuery
            {
                Scope = WorkflowExecutionRelationConstants.Scope,
                RootNodeId = actorIdValue,
                Direction = ProjectionRelationDirection.Both,
                Depth = boundedDepth,
                Take = boundedTake,
            },
            ct);
        return _mapper.ToActorRelationSubgraph(actorIdValue, subgraph);
    }

    private sealed class NoopProjectionRelationStore : IProjectionRelationStore
    {
        public static NoopProjectionRelationStore Instance { get; } = new();

        public Task UpsertNodeAsync(ProjectionRelationNode node, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpsertEdgeAsync(ProjectionRelationEdge edge, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
            ProjectionRelationQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectionRelationEdge>>([]);

        public Task<ProjectionRelationSubgraph> GetSubgraphAsync(
            ProjectionRelationQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(new ProjectionRelationSubgraph());
    }
}
