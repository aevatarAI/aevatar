namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreBinding<TReadModel, TKey>
    : IProjectionStoreBinding<TReadModel, TKey>
    where TReadModel : class, IGraphReadModel
{
    private readonly IProjectionGraphStore _graphStore;

    public ProjectionGraphStoreBinding(IProjectionGraphStore graphStore)
    {
        _graphStore = graphStore;
    }

    public string StoreName => "Graph";

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var scope = NormalizeToken(readModel.GraphScope);
        if (scope.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph scope is required for read model '{typeof(TReadModel).FullName}'.");
        }

        var ownerId = BuildManagedOwnerId(readModel);
        var normalizedNodes = NormalizeNodes(readModel.GraphNodes, scope, ownerId);
        foreach (var node in normalizedNodes)
            await _graphStore.UpsertNodeAsync(node, ct);

        var normalizedEdges = NormalizeEdges(readModel.GraphEdges, scope, ownerId);
        foreach (var edge in normalizedEdges)
            await _graphStore.UpsertEdgeAsync(edge, ct);

        var targetNodeIds = normalizedNodes
            .Select(x => x.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var targetEdgeIds = normalizedEdges
            .Select(x => x.EdgeId)
            .ToHashSet(StringComparer.Ordinal);

        var existingManagedEdges = await _graphStore.ListEdgesByOwnerAsync(scope, ownerId, take: 50000, ct);
        foreach (var edge in existingManagedEdges.Where(IsManagedEdge))
        {
            if (targetEdgeIds.Contains(edge.EdgeId))
                continue;

            await _graphStore.DeleteEdgeAsync(scope, edge.EdgeId, ct);
        }

        var existingManagedNodes = await _graphStore.ListNodesByOwnerAsync(scope, ownerId, take: 50000, ct);
        foreach (var node in existingManagedNodes.Where(IsManagedNode))
        {
            if (targetNodeIds.Contains(node.NodeId))
                continue;
            if (!await CanDeleteNodeAsync(scope, node.NodeId, ct))
                continue;

            await _graphStore.DeleteNodeAsync(scope, node.NodeId, ct);
        }
    }

    private static string BuildManagedOwnerId(IGraphReadModel readModel)
    {
        var readModelId = NormalizeToken(readModel.Id);
        if (readModelId.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph read model '{readModel.GetType().FullName}' requires a non-empty Id for owner lifecycle management.");
        }

        var readModelType = NormalizeToken(readModel.GetType().FullName);
        return readModelType.Length == 0
            ? readModelId
            : $"{readModelType}:{readModelId}";
    }

    private static IReadOnlyList<ProjectionGraphNode> NormalizeNodes(
        IReadOnlyList<ProjectionGraphNode> graphNodes,
        string scope,
        string ownerId)
    {
        if (graphNodes.Count == 0)
            return [];

        var nodesById = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);
        foreach (var sourceNode in graphNodes)
        {
            var nodeId = NormalizeToken(sourceNode.NodeId);
            if (nodeId.Length == 0)
                continue;

            var nodeType = NormalizeToken(sourceNode.NodeType);
            if (nodeType.Length == 0)
                nodeType = "Unknown";

            var properties = new Dictionary<string, string>(sourceNode.Properties, StringComparer.Ordinal)
            {
                [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            nodesById[nodeId] = new ProjectionGraphNode
            {
                Scope = scope,
                NodeId = nodeId,
                NodeType = nodeType,
                Properties = properties,
                UpdatedAt = sourceNode.UpdatedAt == default ? DateTimeOffset.UtcNow : sourceNode.UpdatedAt,
            };
        }

        return nodesById.Values.ToList();
    }

    private static IReadOnlyList<ProjectionGraphEdge> NormalizeEdges(
        IReadOnlyList<ProjectionGraphEdge> graphEdges,
        string scope,
        string ownerId)
    {
        if (graphEdges.Count == 0)
            return [];

        var edgesById = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);
        foreach (var sourceEdge in graphEdges)
        {
            var edgeId = NormalizeToken(sourceEdge.EdgeId);
            var edgeType = NormalizeToken(sourceEdge.EdgeType);
            var fromNodeId = NormalizeToken(sourceEdge.FromNodeId);
            var toNodeId = NormalizeToken(sourceEdge.ToNodeId);
            if (edgeId.Length == 0 ||
                edgeType.Length == 0 ||
                fromNodeId.Length == 0 ||
                toNodeId.Length == 0)
            {
                continue;
            }

            var properties = new Dictionary<string, string>(sourceEdge.Properties, StringComparer.Ordinal)
            {
                [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] = ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            edgesById[edgeId] = new ProjectionGraphEdge
            {
                Scope = scope,
                EdgeId = edgeId,
                EdgeType = edgeType,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                Properties = properties,
                UpdatedAt = sourceEdge.UpdatedAt == default ? DateTimeOffset.UtcNow : sourceEdge.UpdatedAt,
            };
        }

        return edgesById.Values.ToList();
    }

    private static bool IsManagedNode(ProjectionGraphNode node)
    {
        return node.Properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedMarkerKey, out var markerValue) &&
               string.Equals(markerValue, ProjectionGraphManagedPropertyKeys.ManagedMarkerValue, StringComparison.Ordinal);
    }

    private static bool IsManagedEdge(ProjectionGraphEdge edge)
    {
        return edge.Properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedMarkerKey, out var markerValue) &&
               string.Equals(markerValue, ProjectionGraphManagedPropertyKeys.ManagedMarkerValue, StringComparison.Ordinal);
    }

    private async Task<bool> CanDeleteNodeAsync(
        string scope,
        string nodeId,
        CancellationToken ct)
    {
        if (nodeId.Length == 0)
            return false;

        var neighbors = await _graphStore.GetNeighborsAsync(
            new ProjectionGraphQuery
            {
                Scope = scope,
                RootNodeId = nodeId,
                Direction = ProjectionGraphDirection.Both,
                EdgeTypes = [],
                Take = 1,
            },
            ct);
        return neighbors.Count == 0;
    }

    private static string NormalizeToken(string? token) => token?.Trim() ?? "";
}
