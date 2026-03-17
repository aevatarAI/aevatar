namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphWriter<TReadModel>
    : IProjectionGraphWriter<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionGraphStore _graphStore;
    private readonly IProjectionGraphMaterializer<TReadModel> _materializer;

    public ProjectionGraphWriter(
        IProjectionGraphStore graphStore,
        IProjectionGraphMaterializer<TReadModel> materializer)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var materialized = _materializer.Materialize(readModel);
        var scope = NormalizeToken(materialized.Scope);
        if (scope.Length == 0)
        {
            throw new InvalidOperationException(
                $"Graph scope is required for read model '{typeof(TReadModel).FullName}'.");
        }

        var ownerId = BuildOwnerId(readModel);
        return _graphStore.ReplaceOwnerGraphAsync(
            new ProjectionOwnedGraph
            {
                Scope = scope,
                OwnerId = ownerId,
                Nodes = NormalizeNodes(materialized.Nodes, scope, ownerId),
                Edges = NormalizeEdges(materialized.Edges, scope, ownerId),
            },
            ct);
    }

    private static string BuildOwnerId(TReadModel readModel)
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

    private static string NormalizeToken(string? token) => token?.Trim() ?? string.Empty;
}
