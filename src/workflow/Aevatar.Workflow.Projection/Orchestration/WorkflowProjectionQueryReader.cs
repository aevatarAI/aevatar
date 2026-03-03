using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionQueryReader : IWorkflowProjectionQueryReader
{
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly WorkflowExecutionReadModelMapper _mapper;

    public WorkflowProjectionQueryReader(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        WorkflowExecutionReadModelMapper mapper)
    {
        _store = store;
        _mapper = mapper;
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

    public async Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
        string actorId,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        // Graph store removed; topology edges are derived from the read model.
        var report = await _store.GetAsync(actorId, ct);
        if (report == null)
            return [];

        return report.Topology
            .Take(Math.Clamp(take, 1, 1000))
            .Select(e => new WorkflowActorGraphEdge
            {
                FromNodeId = e.Parent,
                ToNodeId = e.Child,
                EdgeType = "CHILD_OF",
            })
            .ToList();
    }

    public async Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        // Graph store removed; return topology edges from the read model.
        var edges = await GetActorGraphEdgesAsync(actorId, take, options, ct);
        var nodeIds = edges
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new WorkflowActorGraphSubgraph
        {
            RootNodeId = actorId,
            Nodes = nodeIds.Select(id => new WorkflowActorGraphNode { NodeId = id }).ToList(),
            Edges = edges.ToList(),
        };
    }

    public async Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        var snapshot = await GetActorSnapshotAsync(actorId, ct);
        if (snapshot == null)
            return null;

        var subgraph = await GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);
        return new WorkflowActorGraphEnrichedSnapshot
        {
            Snapshot = snapshot,
            Subgraph = subgraph,
        };
    }
}
