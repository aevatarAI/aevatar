using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionQueryReader : IWorkflowExecutionProjectionQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _currentStateReader;
    private readonly IProjectionDocumentReader<WorkflowRunTimelineDocument, string> _timelineReader;
    private readonly IProjectionGraphStore _graphStore;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly bool _enableActorQueryEndpoints;

    public WorkflowProjectionQueryReader(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> currentStateReader,
        IProjectionDocumentReader<WorkflowRunTimelineDocument, string> timelineReader,
        WorkflowExecutionReadModelMapper mapper,
        IProjectionGraphStore graphStore,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _currentStateReader = currentStateReader ?? throw new ArgumentNullException(nameof(currentStateReader));
        _timelineReader = timelineReader ?? throw new ArgumentNullException(nameof(timelineReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _enableActorQueryEndpoints = options == null || (options.Enabled && options.EnableActorQueryEndpoints);
    }

    public bool EnableActorQueryEndpoints => _enableActorQueryEndpoints;

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        return currentState == null ? null : _mapper.ToActorSnapshot(currentState);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var currentStates = await _currentStateReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        return currentStates.Items
            .Select(_mapper.ToActorSnapshot)
            .ToList();
    }

    public async Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        return currentState == null ? null : _mapper.ToActorProjectionState(currentState);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var timelineDocument = await _timelineReader.GetAsync(actorId, ct);
        if (timelineDocument == null)
            return [];

        return timelineDocument.Timeline
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
        if (!_enableActorQueryEndpoints)
            return [];

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
        if (!_enableActorQueryEndpoints)
            return new WorkflowActorGraphSubgraph
            {
                RootNodeId = actorId ?? string.Empty,
            };

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
        if (!_enableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

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
