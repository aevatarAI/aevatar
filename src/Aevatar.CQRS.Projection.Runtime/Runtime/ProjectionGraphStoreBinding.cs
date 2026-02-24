namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreBinding<TReadModel, TKey>
    : IProjectionStoreBinding<TReadModel, TKey>,
      IProjectionStoreBindingAvailability
    where TReadModel : class, IProjectionReadModel
{
    private const int CleanupPageSize = 1000;
    private const int CleanupMaxItems = 1_000_000;

    private readonly IProjectionGraphStore? _graphStore;

    public ProjectionGraphStoreBinding(IProjectionGraphStore? graphStore = null)
    {
        _graphStore = graphStore;
    }

    public bool IsConfigured =>
        _graphStore is not null &&
        typeof(IGraphReadModel).IsAssignableFrom(typeof(TReadModel));

    public string StoreName => IsConfigured ? "Graph" : "Graph(Unconfigured)";

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        if (_graphStore is null || readModel is not IGraphReadModel graphReadModel)
            return;

        var scope = NormalizeToken(graphReadModel.GraphScope);
        if (scope.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph scope is required for read model '{typeof(TReadModel).FullName}'.");
        }

        var ownerId = BuildManagedOwnerId(graphReadModel);
        var normalizedNodes = NormalizeNodes(graphReadModel.GraphNodes, scope, ownerId);
        foreach (var node in normalizedNodes)
            await GraphStore.UpsertNodeAsync(node, ct);

        var normalizedEdges = NormalizeEdges(graphReadModel.GraphEdges, scope, ownerId);
        foreach (var edge in normalizedEdges)
            await GraphStore.UpsertEdgeAsync(edge, ct);

        var targetNodeIds = normalizedNodes
            .Select(x => x.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var targetEdgeIds = normalizedEdges
            .Select(x => x.EdgeId)
            .ToHashSet(StringComparer.Ordinal);

        var existingManagedEdges = await ListManagedEdgesByOwnerAsync(scope, ownerId, ct);
        foreach (var edge in existingManagedEdges)
        {
            if (targetEdgeIds.Contains(edge.EdgeId))
                continue;

            await GraphStore.DeleteEdgeAsync(scope, edge.EdgeId, ct);
        }

        var existingManagedNodes = await ListManagedNodesByOwnerAsync(scope, ownerId, ct);
        foreach (var node in existingManagedNodes)
        {
            if (targetNodeIds.Contains(node.NodeId))
                continue;
            if (!await CanDeleteNodeAsync(scope, node.NodeId, ct))
                continue;

            await GraphStore.DeleteNodeAsync(scope, node.NodeId, ct);
        }
    }

    private async Task<IReadOnlyList<ProjectionGraphEdge>> ListManagedEdgesByOwnerAsync(
        string scope,
        string ownerId,
        CancellationToken ct)
    {
        var result = new List<ProjectionGraphEdge>();
        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GraphStore.ListEdgesByOwnerAsync(
                scope,
                ownerId,
                skip: skip,
                take: CleanupPageSize,
                ct);
            if (page.Count == 0)
                break;

            result.AddRange(page.Where(IsManagedEdge));
            if (result.Count > CleanupMaxItems)
            {
                throw new InvalidOperationException(
                    $"Graph cleanup exceeded maximum edge scan limit ({CleanupMaxItems}) for read model '{typeof(TReadModel).FullName}'.");
            }

            if (page.Count < CleanupPageSize)
                break;

            skip += page.Count;
        }

        return result;
    }

    private async Task<IReadOnlyList<ProjectionGraphNode>> ListManagedNodesByOwnerAsync(
        string scope,
        string ownerId,
        CancellationToken ct)
    {
        var result = new List<ProjectionGraphNode>();
        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GraphStore.ListNodesByOwnerAsync(
                scope,
                ownerId,
                skip: skip,
                take: CleanupPageSize,
                ct);
            if (page.Count == 0)
                break;

            result.AddRange(page.Where(IsManagedNode));
            if (result.Count > CleanupMaxItems)
            {
                throw new InvalidOperationException(
                    $"Graph cleanup exceeded maximum node scan limit ({CleanupMaxItems}) for read model '{typeof(TReadModel).FullName}'.");
            }

            if (page.Count < CleanupPageSize)
                break;

            skip += page.Count;
        }

        return result;
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

        var neighbors = await GraphStore.GetNeighborsAsync(
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

    private IProjectionGraphStore GraphStore =>
        _graphStore ??
        throw new InvalidOperationException(
            $"Graph projection store is not configured for read model '{typeof(TReadModel).FullName}'.");
}
