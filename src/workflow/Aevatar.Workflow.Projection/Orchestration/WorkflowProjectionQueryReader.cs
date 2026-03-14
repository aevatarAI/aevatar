using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionQueryReader : IWorkflowProjectionQueryReader
{
    private readonly IProjectionDocumentReader<WorkflowExecutionReport, string> _documentReader;
    private readonly IProjectionGraphStore _graphStore;
    private readonly WorkflowExecutionReadModelMapper _mapper;

    public WorkflowProjectionQueryReader(
        IProjectionDocumentReader<WorkflowExecutionReport, string> documentReader,
        WorkflowExecutionReadModelMapper mapper,
        IProjectionGraphStore graphStore)
    {
        _documentReader = documentReader;
        _mapper = mapper;
        _graphStore = graphStore;
    }

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var report = await _documentReader.GetAsync(actorId, ct);
        return report == null ? null : _mapper.ToActorSnapshot(report);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var reports = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        return reports.Items
            .Select(_mapper.ToActorSnapshot)
            .ToList();
    }

    public async Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var report = await _documentReader.GetAsync(actorId, ct);
        return report == null ? null : _mapper.ToActorProjectionState(report);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var report = await _documentReader.GetAsync(actorId, ct);
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
        var actorIdValue = actorId?.Trim() ?? "";
        if (actorIdValue.Length == 0)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var direction = MapDirection(options?.Direction ?? WorkflowActorGraphDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var edges = await _graphStore.GetNeighborsAsync(
            new ProjectionGraphQuery
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                RootNodeId = actorIdValue,
                Direction = direction,
                EdgeTypes = edgeTypes,
                Take = boundedTake,
            },
            ct);
        return edges.Select(_mapper.ToActorGraphEdge).ToList();
    }

    public async Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
        string actorId,
        int depth = 2,
        int take = 200,
        WorkflowActorGraphQueryOptions? options = null,
        CancellationToken ct = default)
    {
        var actorIdValue = actorId?.Trim() ?? "";
        if (actorIdValue.Length == 0)
            return new WorkflowActorGraphSubgraph();

        var boundedDepth = Math.Clamp(depth, 1, 8);
        var boundedTake = Math.Clamp(take, 1, 2000);
        var direction = MapDirection(options?.Direction ?? WorkflowActorGraphDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var subgraph = await _graphStore.GetSubgraphAsync(
            new ProjectionGraphQuery
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                RootNodeId = actorIdValue,
                Direction = direction,
                EdgeTypes = edgeTypes,
                Depth = boundedDepth,
                Take = boundedTake,
            },
            ct);
        return _mapper.ToActorGraphSubgraph(actorIdValue, subgraph);
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

    private static ProjectionGraphDirection MapDirection(WorkflowActorGraphDirection direction)
    {
        return direction switch
        {
            WorkflowActorGraphDirection.Outbound => ProjectionGraphDirection.Outbound,
            WorkflowActorGraphDirection.Inbound => ProjectionGraphDirection.Inbound,
            _ => ProjectionGraphDirection.Both,
        };
    }

    private static IReadOnlyList<string> NormalizeEdgeTypes(IReadOnlyList<string>? edgeTypes)
    {
        if (edgeTypes == null || edgeTypes.Count == 0)
            return [];

        return edgeTypes
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
