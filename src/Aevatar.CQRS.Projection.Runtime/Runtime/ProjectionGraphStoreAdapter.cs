namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreAdapter<TReadModel>
    : IGraphProjectionStore<TReadModel>
    where TReadModel : class
{
    private const string ManagedMarkerKey = "projectionManaged";
    private const string ManagedMarkerValue = "true";
    private readonly IProjectionRelationStore _relationStore;

    public ProjectionGraphStoreAdapter(IProjectionRelationStore relationStore)
    {
        _relationStore = relationStore;
    }

    public async Task UpsertGraphAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        if (readModel is not IGraphReadModel graphReadModel)
            return;

        var scope = NormalizeToken(graphReadModel.GraphScope);
        if (scope.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph scope is required for read model '{typeof(TReadModel).FullName}'.");
        }

        var normalizedNodes = NormalizeNodes(graphReadModel.GraphNodes, scope);
        foreach (var node in normalizedNodes)
            await _relationStore.UpsertNodeAsync(node, ct);

        var normalizedEdges = NormalizeEdges(graphReadModel.GraphEdges, scope);
        foreach (var edge in normalizedEdges)
            await _relationStore.UpsertEdgeAsync(edge, ct);

        var anchorNodeId = ResolveAnchorNodeId(graphReadModel, normalizedNodes, normalizedEdges);
        if (anchorNodeId.Length == 0)
            return;

        var existing = await _relationStore.GetSubgraphAsync(
            new ProjectionRelationQuery
            {
                Scope = scope,
                RootNodeId = anchorNodeId,
                Direction = ProjectionRelationDirection.Both,
                Depth = 8,
                Take = 5000,
            },
            ct);

        var targetEdgeIds = normalizedEdges
            .Select(x => x.EdgeId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var edge in existing.Edges.Where(IsManagedEdge))
        {
            if (targetEdgeIds.Contains(edge.EdgeId))
                continue;

            await _relationStore.DeleteEdgeAsync(scope, edge.EdgeId, ct);
        }
    }

    public Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default) =>
        _relationStore.GetNeighborsAsync(query, ct);

    public Task<ProjectionRelationSubgraph> GetSubgraphAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default) =>
        _relationStore.GetSubgraphAsync(query, ct);

    private static string ResolveAnchorNodeId(
        IGraphReadModel readModel,
        IReadOnlyList<ProjectionRelationNode> nodes,
        IReadOnlyList<ProjectionRelationEdge> edges)
    {
        var firstNodeId = nodes.FirstOrDefault()?.NodeId ?? "";
        if (firstNodeId.Length > 0)
            return firstNodeId;

        var readModelId = NormalizeToken(readModel.Id);
        if (readModelId.Length > 0)
            return readModelId;

        return edges.FirstOrDefault()?.FromNodeId ?? "";
    }

    private static bool IsManagedEdge(ProjectionRelationEdge edge)
    {
        return edge.Properties.TryGetValue(ManagedMarkerKey, out var markerValue) &&
               string.Equals(markerValue, ManagedMarkerValue, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ProjectionRelationNode> NormalizeNodes(
        IReadOnlyList<GraphNodeDescriptor> graphNodes,
        string scope)
    {
        if (graphNodes.Count == 0)
            return [];

        var nodesById = new Dictionary<string, ProjectionRelationNode>(StringComparer.Ordinal);
        foreach (var graphNode in graphNodes)
        {
            var nodeId = NormalizeToken(graphNode.NodeId);
            if (nodeId.Length == 0)
                continue;

            var nodeType = NormalizeToken(graphNode.NodeType);
            if (nodeType.Length == 0)
                nodeType = "Unknown";

            nodesById[nodeId] = new ProjectionRelationNode
            {
                Scope = scope,
                NodeId = nodeId,
                NodeType = nodeType,
                Properties = new Dictionary<string, string>(graphNode.Properties, StringComparer.Ordinal),
                UpdatedAt = graphNode.UpdatedAt == default ? DateTimeOffset.UtcNow : graphNode.UpdatedAt,
            };
        }

        return nodesById.Values.ToList();
    }

    private static IReadOnlyList<ProjectionRelationEdge> NormalizeEdges(
        IReadOnlyList<GraphEdgeDescriptor> graphEdges,
        string scope)
    {
        if (graphEdges.Count == 0)
            return [];

        var edgesById = new Dictionary<string, ProjectionRelationEdge>(StringComparer.Ordinal);
        foreach (var graphEdge in graphEdges)
        {
            var edgeId = NormalizeToken(graphEdge.EdgeId);
            var relationType = NormalizeToken(graphEdge.RelationType);
            var fromNodeId = NormalizeToken(graphEdge.FromNodeId);
            var toNodeId = NormalizeToken(graphEdge.ToNodeId);
            if (edgeId.Length == 0 ||
                relationType.Length == 0 ||
                fromNodeId.Length == 0 ||
                toNodeId.Length == 0)
            {
                continue;
            }

            var properties = new Dictionary<string, string>(graphEdge.Properties, StringComparer.Ordinal)
            {
                [ManagedMarkerKey] = ManagedMarkerValue,
            };

            edgesById[edgeId] = new ProjectionRelationEdge
            {
                Scope = scope,
                EdgeId = edgeId,
                RelationType = relationType,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                Properties = properties,
                UpdatedAt = graphEdge.UpdatedAt == default ? DateTimeOffset.UtcNow : graphEdge.UpdatedAt,
            };
        }

        return edgesById.Values.ToList();
    }

    private static string NormalizeToken(string? token) => token?.Trim() ?? "";
}
