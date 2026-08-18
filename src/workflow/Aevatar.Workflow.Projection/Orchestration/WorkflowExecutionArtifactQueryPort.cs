using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionArtifactQueryPort : IWorkflowExecutionArtifactQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> _reportReader;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly IProjectionDocumentReader<ProjectionScopeStatusDocument, string>? _scopeStatusReader;
    private readonly IVersionedProjectionGraphStore? _versionedGraphStore;
    private readonly WorkflowRunIncrementalGraphMaterializer? _incrementalGraphMaterializer;
    private readonly bool _workflowArtifactQueryEnabled;

    public WorkflowExecutionArtifactQueryPort(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        WorkflowExecutionReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null,
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string>? scopeStatusReader = null,
        IVersionedProjectionGraphStore? versionedGraphStore = null,
        WorkflowRunIncrementalGraphMaterializer? incrementalGraphMaterializer = null)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _scopeStatusReader = scopeStatusReader;
        _versionedGraphStore = versionedGraphStore;
        _incrementalGraphMaterializer = incrementalGraphMaterializer;
        _workflowArtifactQueryEnabled = options == null || (options.Enabled && options.WorkflowArtifactQueryEnabled);
    }

    public bool WorkflowArtifactQueryEnabled => _workflowArtifactQueryEnabled;

    public async Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(
        string workflowRunId,
        CancellationToken ct = default)
    {
        if (!_workflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
            return null;

        var report = await _reportReader.GetAsync(workflowRunId, ct);
        return report == null ? null : _mapper.ToRunReport(report);
    }

    public async Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
        string workflowRunId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_workflowArtifactQueryEnabled || string.IsNullOrWhiteSpace(workflowRunId))
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var report = await _reportReader.GetAsync(workflowRunId, ct);
        if (report == null)
            return [];

        return report.Timeline
            .OrderByDescending(x => x.Timestamp)
            .Take(boundedTake)
            .Select(_mapper.ToWorkflowRunTimelineExportItem)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>> GetWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_workflowArtifactQueryEnabled)
            return [];

        var ownerId = workflowRunId?.Trim() ?? string.Empty;
        if (ownerId.Length == 0)
            return [];

        var consistent = await ReadConsistentSnapshotAsync(ownerId, ct);
        if (consistent == null)
            return [];

        var filtered = FilterSnapshot(
            consistent.Snapshot,
            ownerId,
            depth: 1,
            Math.Clamp(take, 1, 1000),
            options);
        return filtered.Edges.Select(_mapper.ToWorkflowRunGraphExportEdge).ToList();
    }

    public async Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
        string workflowRunId,
        int depth = 2,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        var ownerId = workflowRunId?.Trim() ?? string.Empty;
        if (!_workflowArtifactQueryEnabled || ownerId.Length == 0)
            return Unavailable(ownerId);

        var consistent = await ReadConsistentSnapshotAsync(ownerId, ct);
        if (consistent == null)
            return Unavailable(ownerId);

        var filtered = FilterSnapshot(
            consistent.Snapshot,
            ownerId,
            Math.Clamp(depth, 1, 8),
            Math.Clamp(take, 1, 2000),
            options);
        var result = _mapper.ToWorkflowRunGraphExportSubgraph(
            ownerId,
            filtered,
            consistent.Snapshot.Source.StateVersion);
        result.RouteFingerprint = new WorkflowRunGraphExportRouteFingerprint
        {
            ContractId = consistent.Route.ContractId,
            ContractVersion = consistent.Route.ContractVersion,
            PhysicalNamespace = consistent.Route.PhysicalNamespace,
            RouteEpoch = consistent.Route.RouteEpoch,
        };
        result.SourceCoordinate = new WorkflowRunGraphExportSourceCoordinate
        {
            ActorId = consistent.Snapshot.Source.ActorId,
            StateVersion = consistent.Snapshot.Source.StateVersion,
            EventId = consistent.Snapshot.Source.EventId,
        };
        return result;
    }

    private async Task<ConsistentGraphSnapshot?> ReadConsistentSnapshotAsync(
        string ownerId,
        CancellationToken ct)
    {
        if (_scopeStatusReader == null ||
            _versionedGraphStore == null ||
            _incrementalGraphMaterializer == null)
        {
            return null;
        }

        var statusId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            ownerId,
            WorkflowProjectionKinds.ExecutionMaterialization,
            ProjectionRuntimeMode.DurableMaterialization));
        var before = await _scopeStatusReader.GetAsync(statusId, ct);
        var route = before?.ActiveMaterializationRoute;
        if (!IsReadableIncrementalStatus(before, route))
            return null;

        var storeRoute = _incrementalGraphMaterializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            ownerId,
            route!);
        var read = await _versionedGraphStore.ReadOwnerSnapshotAsync(storeRoute, ct);
        if (read.Disposition != ProjectionGraphOwnerSnapshotReadDisposition.Found ||
            read.Snapshot?.Source == null ||
            read.Snapshot.Route == null ||
            !ProjectionGraphDeltaContract.RouteEquals(storeRoute, read.Snapshot.Route) ||
            read.Snapshot.Source.StateVersion <= 0 ||
            !string.Equals(read.Snapshot.Source.ActorId, ownerId, StringComparison.Ordinal))
        {
            return null;
        }

        var after = await _scopeStatusReader.GetAsync(statusId, ct);
        if (!IsReadableIncrementalStatus(after, after?.ActiveMaterializationRoute) ||
            !RouteEquals(route!, after!.ActiveMaterializationRoute!))
        {
            return null;
        }

        return new ConsistentGraphSnapshot(route!.Clone(), read.Snapshot);
    }

    private static bool IsReadableIncrementalStatus(
        ProjectionScopeStatusDocument? status,
        ProjectionMaterializationRouteFingerprint? route) =>
        status is { Active: true, Released: false } &&
        WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(route);

    private static bool RouteEquals(
        ProjectionMaterializationRouteFingerprint left,
        ProjectionMaterializationRouteFingerprint right) =>
        left.RouteEpoch == right.RouteEpoch &&
        left.ContractVersion == right.ContractVersion &&
        string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
        string.Equals(left.PhysicalNamespace, right.PhysicalNamespace, StringComparison.Ordinal);

    private static ProjectionGraphSubgraph FilterSnapshot(
        ProjectionGraphOwnerSnapshot snapshot,
        string rootNodeId,
        int depth,
        int take,
        WorkflowRunGraphExportQueryOptions? options)
    {
        var direction = NormalizeDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var allEdges = snapshot.Edges
            .Where(edge => edgeTypes.Count == 0 || edgeTypes.Contains(edge.EdgeType))
            .ToArray();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var selectedEdges = new Dictionary<string, ProjectionGraphEdgeMutation>(StringComparer.Ordinal);
        for (var level = 0; level < depth && frontier.Count > 0 && selectedEdges.Count < take; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in allEdges)
            {
                var outbound =
                    (direction is WorkflowRunGraphExportDirection.Outbound or WorkflowRunGraphExportDirection.Both) &&
                    frontier.Contains(edge.FromNodeId);
                var inbound =
                    (direction is WorkflowRunGraphExportDirection.Inbound or WorkflowRunGraphExportDirection.Both) &&
                    frontier.Contains(edge.ToNodeId);
                if (!outbound && !inbound)
                    continue;

                selectedEdges.TryAdd(edge.EdgeId, edge);
                if (selectedEdges.Count >= take)
                    break;
                if (outbound && visited.Add(edge.ToNodeId))
                    next.Add(edge.ToNodeId);
                if (inbound && visited.Add(edge.FromNodeId))
                    next.Add(edge.FromNodeId);
            }

            frontier = next;
        }

        return new ProjectionGraphSubgraph
        {
            Nodes = snapshot.Nodes
                .Where(node => visited.Contains(node.NodeId))
                .Take(take)
                .Select(ToGraphNode)
                .ToArray(),
            Edges = selectedEdges.Values
                .Take(take)
                .Select(ToGraphEdge)
                .ToArray(),
        };
    }

    private static ProjectionGraphNode ToGraphNode(ProjectionGraphNodeMutation source)
    {
        var node = new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = source.NodeId,
            NodeType = source.NodeType,
            UpdatedAt = ResolveTimestamp(source.UpdatedAtEpochMs),
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
        return node;
    }

    private static ProjectionGraphEdge ToGraphEdge(ProjectionGraphEdgeMutation source)
    {
        var edge = new ProjectionGraphEdge
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            EdgeId = source.EdgeId,
            FromNodeId = source.FromNodeId,
            ToNodeId = source.ToNodeId,
            EdgeType = source.EdgeType,
            UpdatedAt = ResolveTimestamp(source.UpdatedAtEpochMs),
            Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal),
        };
        return edge;
    }

    private static DateTimeOffset ResolveTimestamp(long epochMilliseconds) =>
        epochMilliseconds <= 0 ? default : DateTimeOffset.FromUnixTimeMilliseconds(epochMilliseconds);

    private static HashSet<string> NormalizeEdgeTypes(IReadOnlyList<string>? edgeTypes) =>
        edgeTypes == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : edgeTypes
                .Select(static value => value?.Trim() ?? string.Empty)
                .Where(static value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

    private static WorkflowRunGraphExportDirection NormalizeDirection(
        WorkflowRunGraphExportDirection direction) =>
        direction is WorkflowRunGraphExportDirection.Outbound or WorkflowRunGraphExportDirection.Inbound
            ? direction
            : WorkflowRunGraphExportDirection.Both;

    private static WorkflowRunGraphExportSubgraph Unavailable(string rootNodeId) =>
        new()
        {
            RootNodeId = rootNodeId,
        };

    private sealed record ConsistentGraphSnapshot(
        ProjectionMaterializationRouteFingerprint Route,
        ProjectionGraphOwnerSnapshot Snapshot);
}
