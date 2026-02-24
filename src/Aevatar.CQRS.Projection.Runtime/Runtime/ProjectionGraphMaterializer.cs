namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphMaterializer<TReadModel>
    : IProjectionGraphMaterializer<TReadModel>
    where TReadModel : class
{
    private readonly IProjectionGraphStore _graphStore;

    public ProjectionGraphMaterializer(IProjectionGraphStore graphStore)
    {
        _graphStore = graphStore;
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

        var ownerResolution = BuildManagedOwnerId(graphReadModel);
        var ownerId = ownerResolution.OwnerId;

        var normalizedNodes = NormalizeNodes(graphReadModel.GraphNodes, scope, ownerId);
        foreach (var node in normalizedNodes)
            await _graphStore.UpsertNodeAsync(node, ct);

        var normalizedEdges = NormalizeEdges(graphReadModel.GraphEdges, scope, ownerId);
        foreach (var edge in normalizedEdges)
            await _graphStore.UpsertEdgeAsync(edge, ct);

        var targetEdgeIds = normalizedEdges
            .Select(x => x.EdgeId)
            .ToHashSet(StringComparer.Ordinal);
        if (!ownerResolution.CanCleanup)
            return;

        var targetNodeIds = normalizedNodes
            .Select(x => x.NodeId)
            .Where(x => x.Length > 0)
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

    private static bool IsManagedEdge(ProjectionGraphEdge edge)
    {
        return edge.Properties.TryGetValue(ProjectionGraphSystemPropertyKeys.ManagedMarkerKey, out var markerValue) &&
               string.Equals(markerValue, ProjectionGraphSystemPropertyKeys.ManagedMarkerValue, StringComparison.Ordinal);
    }

    private static bool IsManagedNode(ProjectionGraphNode node)
    {
        return node.Properties.TryGetValue(ProjectionGraphSystemPropertyKeys.ManagedMarkerKey, out var markerValue) &&
               string.Equals(markerValue, ProjectionGraphSystemPropertyKeys.ManagedMarkerValue, StringComparison.Ordinal);
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

    private static ManagedOwnerResolution BuildManagedOwnerId(IGraphReadModel readModel)
    {
        var readModelId = NormalizeToken(readModel.Id);
        var canCleanup = readModelId.Length > 0;
        if (readModelId.Length == 0)
        {
            readModelId = readModel.GraphNodes
                .Select(x => NormalizeToken(x.NodeId))
                .FirstOrDefault(x => x.Length > 0) ?? "";
            canCleanup = readModelId.Length > 0;
        }

        if (readModelId.Length == 0)
        {
            readModelId = readModel.GraphEdges
                .Select(x => NormalizeToken(x.FromNodeId))
                .FirstOrDefault(x => x.Length > 0) ?? "";
            canCleanup = readModelId.Length > 0;
        }

        if (readModelId.Length == 0)
            readModelId = "unknown";

        var readModelType = NormalizeToken(readModel.GetType().FullName);
        var ownerId = readModelType.Length == 0
            ? readModelId
            : $"{readModelType}:{readModelId}";
        return new ManagedOwnerResolution(ownerId, canCleanup);
    }

    private static IReadOnlyList<ProjectionGraphNode> NormalizeNodes(
        IReadOnlyList<GraphNodeDescriptor> graphNodes,
        string scope,
        string ownerId)
    {
        if (graphNodes.Count == 0)
            return [];

        var nodesById = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal);
        foreach (var graphNode in graphNodes)
        {
            var nodeId = NormalizeToken(graphNode.NodeId);
            if (nodeId.Length == 0)
                continue;

            var nodeType = NormalizeToken(graphNode.NodeType);
            if (nodeType.Length == 0)
                nodeType = "Unknown";

            var properties = new Dictionary<string, string>(graphNode.Properties, StringComparer.Ordinal)
            {
                [ProjectionGraphSystemPropertyKeys.ManagedMarkerKey] = ProjectionGraphSystemPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphSystemPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            nodesById[nodeId] = new ProjectionGraphNode
            {
                Scope = scope,
                NodeId = nodeId,
                NodeType = nodeType,
                Properties = properties,
                UpdatedAt = graphNode.UpdatedAt == default ? DateTimeOffset.UtcNow : graphNode.UpdatedAt,
            };
        }

        return nodesById.Values.ToList();
    }

    private static IReadOnlyList<ProjectionGraphEdge> NormalizeEdges(
        IReadOnlyList<GraphEdgeDescriptor> graphEdges,
        string scope,
        string ownerId)
    {
        if (graphEdges.Count == 0)
            return [];

        var edgesById = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);
        foreach (var graphEdge in graphEdges)
        {
            var edgeId = NormalizeToken(graphEdge.EdgeId);
            var relationType = NormalizeToken(graphEdge.EdgeType);
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
                [ProjectionGraphSystemPropertyKeys.ManagedMarkerKey] = ProjectionGraphSystemPropertyKeys.ManagedMarkerValue,
                [ProjectionGraphSystemPropertyKeys.ManagedOwnerIdKey] = ownerId,
            };

            edgesById[edgeId] = new ProjectionGraphEdge
            {
                Scope = scope,
                EdgeId = edgeId,
                EdgeType = relationType,
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                Properties = properties,
                UpdatedAt = graphEdge.UpdatedAt == default ? DateTimeOffset.UtcNow : graphEdge.UpdatedAt,
            };
        }

        return edgesById.Values.ToList();
    }

    private static string NormalizeToken(string? token) => token?.Trim() ?? "";

    private readonly record struct ManagedOwnerResolution(
        string OwnerId,
        bool CanCleanup);
}
