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
    private readonly IProjectionGraphStore? _legacyGraphStore;
    private readonly bool _workflowArtifactQueryEnabled;
    private readonly bool _workflowGraphExportEnabled;

    public WorkflowExecutionArtifactQueryPort(
        IProjectionDocumentReader<WorkflowRunInsightReportDocument, string> reportReader,
        WorkflowExecutionReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null,
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string>? scopeStatusReader = null,
        IVersionedProjectionGraphStore? versionedGraphStore = null,
        WorkflowRunIncrementalGraphMaterializer? incrementalGraphMaterializer = null,
        IProjectionGraphStore? legacyGraphStore = null,
        ProjectionGraphProviderStatus? graphProviderStatus = null)
    {
        _reportReader = reportReader ?? throw new ArgumentNullException(nameof(reportReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _scopeStatusReader = scopeStatusReader;
        _versionedGraphStore = versionedGraphStore;
        _incrementalGraphMaterializer = incrementalGraphMaterializer;
        _legacyGraphStore = legacyGraphStore;
        _workflowArtifactQueryEnabled = options == null || (options.Enabled && options.WorkflowArtifactQueryEnabled);
        _workflowGraphExportEnabled =
            _workflowArtifactQueryEnabled && graphProviderStatus is not { Enabled: false };
    }

    public bool WorkflowArtifactQueryEnabled => _workflowArtifactQueryEnabled;

    public bool WorkflowGraphExportEnabled => _workflowGraphExportEnabled;

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
            .Select(item => _mapper.ToWorkflowRunTimelineExportItem(item, report))
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowRunGraphExportEdge>> GetWorkflowRunGraphExportEdgesAsync(
        string workflowRunId,
        int take = 200,
        WorkflowRunGraphExportQueryOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_workflowGraphExportEnabled)
            return [];

        var ownerId = workflowRunId?.Trim() ?? string.Empty;
        if (ownerId.Length == 0)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var route = await ResolveGraphRouteAsync(ownerId, ct);
        if (route.Kind == GraphRouteKind.Legacy)
        {
            var edges = await _legacyGraphStore!.GetNeighborsAsync(
                new ProjectionGraphQuery
                {
                    Scope = WorkflowExecutionGraphConstants.Scope,
                    RootNodeId = ownerId,
                    Direction = MapDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both),
                    EdgeTypes = NormalizeEdgeTypes(options?.EdgeTypes).ToArray(),
                    Take = boundedTake,
                },
                ct);
            return edges.Select(_mapper.ToWorkflowRunGraphExportEdge).ToList();
        }

        var consistent = await ReadConsistentSnapshotAsync(ownerId, route, ct);
        if (consistent == null)
            return [];

        var filtered = FilterSnapshot(
            consistent.Snapshot,
            ownerId,
            depth: 1,
            boundedTake,
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
        if (!_workflowGraphExportEnabled || ownerId.Length == 0)
            return Unavailable(ownerId);

        var boundedDepth = Math.Clamp(depth, 1, 8);
        var boundedTake = Math.Clamp(take, 1, 2000);
        var route = await ResolveGraphRouteAsync(ownerId, ct);
        if (route.Kind == GraphRouteKind.Legacy)
        {
            var legacySourceStateVersion = await ResolveLegacyGraphSourceStateVersionAsync(ownerId, ct);
            var legacySubgraph = await _legacyGraphStore!.GetSubgraphAsync(
                new ProjectionGraphQuery
                {
                    Scope = WorkflowExecutionGraphConstants.Scope,
                    RootNodeId = ownerId,
                    Direction = MapDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both),
                    EdgeTypes = NormalizeEdgeTypes(options?.EdgeTypes).ToArray(),
                    Depth = boundedDepth,
                    Take = boundedTake,
                },
                ct);
            return _mapper.ToWorkflowRunGraphExportSubgraph(ownerId, legacySubgraph, legacySourceStateVersion);
        }

        var consistent = await ReadConsistentSnapshotAsync(ownerId, route, ct);
        if (consistent == null)
            return Unavailable(ownerId);

        var filtered = FilterSnapshot(
            consistent.Snapshot,
            ownerId,
            boundedDepth,
            boundedTake,
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

    /// <summary>
    /// The scope actor's committed route (materialized on the scope status document) decides
    /// which store is authoritative for an owner's graph. A missing status document or a
    /// compatibility route means the owner never cut over: its graph lives in the legacy scope
    /// graph store and is read there, exactly as before the incremental route existed. An
    /// incremental route reads only the versioned owner snapshot and never falls back.
    /// </summary>
    private async Task<GraphRoute> ResolveGraphRouteAsync(string ownerId, CancellationToken ct)
    {
        if (_scopeStatusReader == null ||
            _versionedGraphStore == null ||
            _incrementalGraphMaterializer == null)
        {
            return _legacyGraphStore == null
                ? GraphRoute.Unavailable
                : GraphRoute.Legacy;
        }

        var statusId = BuildStatusId(ownerId);
        var status = await _scopeStatusReader.GetAsync(statusId, ct);
        var route = status?.ActiveMaterializationRoute;
        if (WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(route))
        {
            return IsReadableIncrementalStatus(status, route)
                ? new GraphRoute(GraphRouteKind.Incremental, statusId, route!.Clone())
                : GraphRoute.Unavailable;
        }

        return _legacyGraphStore == null
            ? GraphRoute.Unavailable
            : GraphRoute.Legacy;
    }

    private async Task<ConsistentGraphSnapshot?> ReadConsistentSnapshotAsync(
        string ownerId,
        GraphRoute graphRoute,
        CancellationToken ct)
    {
        if (graphRoute.Kind != GraphRouteKind.Incremental ||
            _scopeStatusReader == null ||
            _versionedGraphStore == null ||
            _incrementalGraphMaterializer == null)
        {
            return null;
        }

        var route = graphRoute.Route!;
        var storeRoute = _incrementalGraphMaterializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            ownerId,
            route);
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

        var after = await _scopeStatusReader.GetAsync(graphRoute.StatusId, ct);
        if (!IsReadableIncrementalStatus(after, after?.ActiveMaterializationRoute) ||
            !RouteEquals(route, after!.ActiveMaterializationRoute!))
        {
            return null;
        }

        return new ConsistentGraphSnapshot(route.Clone(), read.Snapshot);
    }

    private static string BuildStatusId(string ownerId) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            ownerId,
            WorkflowProjectionKinds.ExecutionMaterialization,
            ProjectionRuntimeMode.DurableMaterialization));

    private async Task<long> ResolveLegacyGraphSourceStateVersionAsync(string workflowRunId, CancellationToken ct)
    {
        var sourceSubgraph = await _legacyGraphStore!.GetSubgraphAsync(
            new ProjectionGraphQuery
            {
                Scope = WorkflowExecutionGraphConstants.Scope,
                RootNodeId = workflowRunId,
                Direction = ProjectionGraphDirection.Outbound,
                EdgeTypes = [WorkflowExecutionGraphConstants.EdgeTypeOwns],
                Depth = 1,
                Take = 2000,
            },
            ct);

        var sourceVersions = sourceSubgraph.Nodes
            .Where(node => string.Equals(node.NodeType, WorkflowExecutionGraphConstants.RunNodeType, StringComparison.Ordinal))
            .Where(node =>
                node.Properties.TryGetValue(WorkflowExecutionGraphConstants.RootActorIdPropertyKey, out var rootActorId) &&
                string.Equals(rootActorId, workflowRunId, StringComparison.Ordinal))
            .Select(ReadSourceStateVersion)
            .Where(version => version > 0)
            .Distinct()
            .ToList();

        return sourceVersions.Count == 1 ? sourceVersions[0] : 0;
    }

    private static long ReadSourceStateVersion(ProjectionGraphNode node) =>
        node.Properties.TryGetValue(WorkflowExecutionGraphConstants.SourceStateVersionPropertyKey, out var value) &&
        long.TryParse(value, out var parsed) &&
        parsed > 0
            ? parsed
            : 0;

    private static ProjectionGraphDirection MapDirection(WorkflowRunGraphExportDirection direction) =>
        direction switch
        {
            WorkflowRunGraphExportDirection.Outbound => ProjectionGraphDirection.Outbound,
            WorkflowRunGraphExportDirection.Inbound => ProjectionGraphDirection.Inbound,
            _ => ProjectionGraphDirection.Both,
        };

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

    /// <summary>
    /// Deterministic breadth-first neighbourhood of <paramref name="rootNodeId"/> over the owner
    /// snapshot. <paramref name="take"/> is the edge budget: at most <c>take</c> edges are
    /// returned, chosen level by level in the snapshot's stable edge order, and the returned
    /// nodes are exactly the root plus every endpoint of a returned edge (so at most
    /// <c>take + 1</c> nodes and never a dangling edge reference). An edge is only selected
    /// when it still fits the budget and both of its endpoints exist in the snapshot.
    /// </summary>
    private static ProjectionGraphSubgraph FilterSnapshot(
        ProjectionGraphOwnerSnapshot snapshot,
        string rootNodeId,
        int depth,
        int take,
        WorkflowRunGraphExportQueryOptions? options)
    {
        var direction = NormalizeDirection(options?.Direction ?? WorkflowRunGraphExportDirection.Both);
        var edgeTypes = NormalizeEdgeTypes(options?.EdgeTypes);
        var nodesById = snapshot.Nodes
            .GroupBy(static node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var allEdges = snapshot.Edges
            .Where(edge => edgeTypes.Count == 0 || edgeTypes.Contains(edge.EdgeType))
            .Where(edge => nodesById.ContainsKey(edge.FromNodeId) && nodesById.ContainsKey(edge.ToNodeId))
            .OrderBy(static edge => edge.EdgeId, StringComparer.Ordinal)
            .ToArray();
        var visited = new List<string> { rootNodeId };
        var visitedSet = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var selectedEdges = new List<ProjectionGraphEdgeMutation>();
        var selectedEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        var budgetExhausted = false;
        for (var level = 0; level < depth && frontier.Count > 0 && !budgetExhausted; level++)
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
                if ((!outbound && !inbound) || selectedEdgeIds.Contains(edge.EdgeId))
                    continue;
                if (selectedEdges.Count >= take)
                {
                    budgetExhausted = true;
                    break;
                }

                selectedEdgeIds.Add(edge.EdgeId);
                selectedEdges.Add(edge);
                // Both endpoints of every selected edge are part of the result, whichever side
                // the traversal reached it from; only newly discovered nodes extend the frontier.
                foreach (var endpoint in new[] { edge.FromNodeId, edge.ToNodeId })
                {
                    if (!visitedSet.Add(endpoint))
                        continue;
                    visited.Add(endpoint);
                    next.Add(endpoint);
                }
            }

            frontier = next;
        }

        return new ProjectionGraphSubgraph
        {
            Nodes = visited
                .Where(nodesById.ContainsKey)
                .Select(nodeId => ToGraphNode(nodesById[nodeId]))
                .ToArray(),
            Edges = selectedEdges
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

    private enum GraphRouteKind
    {
        Unavailable = 0,
        Legacy = 1,
        Incremental = 2,
    }

    private sealed record GraphRoute(
        GraphRouteKind Kind,
        string StatusId,
        ProjectionMaterializationRouteFingerprint? Route)
    {
        public static readonly GraphRoute Unavailable = new(GraphRouteKind.Unavailable, string.Empty, null);

        public static readonly GraphRoute Legacy = new(GraphRouteKind.Legacy, string.Empty, null);
    }
}
