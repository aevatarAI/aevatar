using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class InMemoryProjectionRelationStore
    : IProjectionRelationStore,
      IProjectionStoreProviderMetadata
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectionRelationNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectionRelationEdge> _edges = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new();

    public InMemoryProjectionRelationStore(
        string providerName = ProjectionReadModelProviderNames.InMemory)
    {
        ProviderCapabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing: false,
            supportsRelations: true,
            supportsRelationTraversal: true);
    }

    public ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }

    public Task UpsertNodeAsync(ProjectionRelationNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(node.Scope);
        var nodeId = NormalizeToken(node.NodeId);
        if (scope.Length == 0 || nodeId.Length == 0)
            throw new InvalidOperationException("Relation node requires non-empty scope and nodeId.");

        var key = BuildNodeKey(scope, nodeId);
        var clone = CloneNode(node, scope, nodeId);
        lock (_gate)
            _nodes[key] = clone;
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(ProjectionRelationEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(edge.Scope);
        var edgeId = NormalizeToken(edge.EdgeId);
        var fromNodeId = NormalizeToken(edge.FromNodeId);
        var toNodeId = NormalizeToken(edge.ToNodeId);
        var relationType = NormalizeToken(edge.RelationType);
        if (scope.Length == 0 || edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0 || relationType.Length == 0)
            throw new InvalidOperationException("Relation edge requires non-empty scope/edgeId/fromNodeId/toNodeId/relationType.");

        var key = BuildEdgeKey(scope, edgeId);
        var clone = CloneEdge(edge, scope, edgeId, fromNodeId, toNodeId, relationType);
        lock (_gate)
            _edges[key] = clone;
        return Task.CompletedTask;
    }

    public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = NormalizeToken(scope);
        var edgeValue = NormalizeToken(edgeId);
        if (scopeValue.Length == 0 || edgeValue.Length == 0)
            return Task.CompletedTask;

        lock (_gate)
            _edges.Remove(BuildEdgeKey(scopeValue, edgeValue));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return Task.FromResult<IReadOnlyList<ProjectionRelationEdge>>([]);

        var relationTypes = NormalizeRelationTypes(query.RelationTypes);
        var take = Math.Clamp(query.Take, 1, 5000);
        List<ProjectionRelationEdge> edges;
        lock (_gate)
        {
            edges = _edges.Values
                .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                .Where(x => relationTypes.Count == 0 || relationTypes.Contains(x.RelationType))
                .Where(x => MatchesDirection(x, rootNodeId, query.Direction))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(take)
                .Select(CloneEdge)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<ProjectionRelationEdge>>(edges);
    }

    public Task<ProjectionRelationSubgraph> GetSubgraphAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return Task.FromResult(new ProjectionRelationSubgraph());

        var relationTypes = NormalizeRelationTypes(query.RelationTypes);
        var depth = Math.Clamp(query.Depth, 1, 8);
        var take = Math.Clamp(query.Take, 1, 5000);

        var visitedNodeIds = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var collectedEdges = new Dictionary<string, ProjectionRelationEdge>(StringComparer.Ordinal);

        for (var currentDepth = 0; currentDepth < depth; currentDepth++)
        {
            if (frontier.Count == 0 || collectedEdges.Count >= take)
                break;

            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in frontier)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<ProjectionRelationEdge> neighbors;
                lock (_gate)
                {
                    neighbors = _edges.Values
                        .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                        .Where(x => relationTypes.Count == 0 || relationTypes.Contains(x.RelationType))
                        .Where(x => MatchesDirection(x, nodeId, query.Direction))
                        .OrderByDescending(x => x.UpdatedAt)
                        .ToList();
                }

                foreach (var edge in neighbors)
                {
                    if (collectedEdges.Count >= take)
                        break;

                    if (!collectedEdges.ContainsKey(edge.EdgeId))
                        collectedEdges[edge.EdgeId] = CloneEdge(edge);

                    var counterpartNodeId = ResolveCounterpartNodeId(edge, nodeId);
                    if (counterpartNodeId.Length == 0)
                        continue;

                    if (visitedNodeIds.Add(counterpartNodeId))
                        nextFrontier.Add(counterpartNodeId);
                }
            }

            frontier = nextFrontier;
        }

        List<ProjectionRelationNode> nodes;
        lock (_gate)
        {
            nodes = visitedNodeIds
                .Select(x =>
                {
                    var key = BuildNodeKey(scope, x);
                    if (_nodes.TryGetValue(key, out var existing))
                        return CloneNode(existing);

                    return new ProjectionRelationNode
                    {
                        Scope = scope,
                        NodeId = x,
                        NodeType = "Unknown",
                        Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                })
                .ToList();
        }

        var graph = new ProjectionRelationSubgraph
        {
            Nodes = nodes,
            Edges = collectedEdges.Values.ToList(),
        };
        return Task.FromResult(graph);
    }

    private bool MatchesDirection(
        ProjectionRelationEdge edge,
        string rootNodeId,
        ProjectionRelationDirection direction)
    {
        return direction switch
        {
            ProjectionRelationDirection.Outbound => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal),
            ProjectionRelationDirection.Inbound => string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
            _ => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal) ||
                 string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
        };
    }

    private static string ResolveCounterpartNodeId(ProjectionRelationEdge edge, string nodeId)
    {
        if (string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal))
            return edge.ToNodeId;
        if (string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal))
            return edge.FromNodeId;
        return "";
    }

    private static HashSet<string> NormalizeRelationTypes(IReadOnlyList<string> relationTypes)
    {
        return relationTypes
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private string BuildNodeKey(string scope, string nodeId) => $"{scope}:{nodeId}";

    private string BuildEdgeKey(string scope, string edgeId) => $"{scope}:{edgeId}";

    private ProjectionRelationNode CloneNode(ProjectionRelationNode source) =>
        CloneNode(source, source.Scope, source.NodeId);

    private ProjectionRelationNode CloneNode(ProjectionRelationNode source, string scope, string nodeId)
    {
        var payload = JsonSerializer.Serialize(source, _jsonOptions);
        var clone = JsonSerializer.Deserialize<ProjectionRelationNode>(payload, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone relation node.");
        clone.Scope = scope;
        clone.NodeId = nodeId;
        return clone;
    }

    private ProjectionRelationEdge CloneEdge(ProjectionRelationEdge source) =>
        CloneEdge(source, source.Scope, source.EdgeId, source.FromNodeId, source.ToNodeId, source.RelationType);

    private ProjectionRelationEdge CloneEdge(
        ProjectionRelationEdge source,
        string scope,
        string edgeId,
        string fromNodeId,
        string toNodeId,
        string relationType)
    {
        var payload = JsonSerializer.Serialize(source, _jsonOptions);
        var clone = JsonSerializer.Deserialize<ProjectionRelationEdge>(payload, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone relation edge.");
        clone.Scope = scope;
        clone.EdgeId = edgeId;
        clone.FromNodeId = fromNodeId;
        clone.ToNodeId = toNodeId;
        clone.RelationType = relationType;
        return clone;
    }

    private static string NormalizeToken(string token) => token?.Trim() ?? "";
}
