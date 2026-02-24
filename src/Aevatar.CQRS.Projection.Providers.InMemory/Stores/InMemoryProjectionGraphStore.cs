using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class InMemoryProjectionGraphStore
    : IProjectionGraphStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectionGraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectionGraphEdge> _edges = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new();

    public InMemoryProjectionGraphStore()
    {
    }

    public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(node.Scope);
        var nodeId = NormalizeToken(node.NodeId);
        if (scope.Length == 0 || nodeId.Length == 0)
            throw new InvalidOperationException("Graph node requires non-empty scope and nodeId.");

        var key = BuildNodeKey(scope, nodeId);
        var clone = CloneNode(node, scope, nodeId);
        lock (_gate)
            _nodes[key] = clone;
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(edge.Scope);
        var edgeId = NormalizeToken(edge.EdgeId);
        var fromNodeId = NormalizeToken(edge.FromNodeId);
        var toNodeId = NormalizeToken(edge.ToNodeId);
        var relationType = NormalizeToken(edge.EdgeType);
        if (scope.Length == 0 || edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0 || relationType.Length == 0)
            throw new InvalidOperationException("Graph edge requires non-empty scope/edgeId/fromNodeId/toNodeId/relationType.");

        var key = BuildEdgeKey(scope, edgeId);
        var clone = CloneEdge(edge, scope, edgeId, fromNodeId, toNodeId, relationType);
        lock (_gate)
            _edges[key] = clone;
        return Task.CompletedTask;
    }

    public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = NormalizeToken(scope);
        var nodeValue = NormalizeToken(nodeId);
        if (scopeValue.Length == 0 || nodeValue.Length == 0)
            return Task.CompletedTask;

        lock (_gate)
            _nodes.Remove(BuildNodeKey(scopeValue, nodeValue));
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

    public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = NormalizeToken(scope);
        var ownerValue = NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>([]);

        var boundedTake = Math.Clamp(take, 1, 50000);
        List<ProjectionGraphNode> nodes;
        lock (_gate)
        {
            nodes = _nodes.Values
                .Where(x => string.Equals(x.Scope, scopeValue, StringComparison.Ordinal))
                .Where(x =>
                    x.Properties.TryGetValue(ProjectionGraphSystemPropertyKeys.ManagedOwnerIdKey, out var nodeOwnerId) &&
                    string.Equals(NormalizeToken(nodeOwnerId), ownerValue, StringComparison.Ordinal))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(boundedTake)
                .Select(CloneNode)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>(nodes);
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = NormalizeToken(scope);
        var ownerValue = NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

        var boundedTake = Math.Clamp(take, 1, 50000);
        List<ProjectionGraphEdge> edges;
        lock (_gate)
        {
            edges = _edges.Values
                .Where(x => string.Equals(x.Scope, scopeValue, StringComparison.Ordinal))
                .Where(x =>
                    x.Properties.TryGetValue(ProjectionGraphSystemPropertyKeys.ManagedOwnerIdKey, out var edgeOwnerId) &&
                    string.Equals(NormalizeToken(edgeOwnerId), ownerValue, StringComparison.Ordinal))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(boundedTake)
                .Select(CloneEdge)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>(edges);
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);

        var edgeTypes = NormalizeEdgeTypes(query.EdgeTypes);
        var take = Math.Clamp(query.Take, 1, 5000);
        List<ProjectionGraphEdge> edges;
        lock (_gate)
        {
            edges = _edges.Values
                .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                .Where(x => edgeTypes.Count == 0 || edgeTypes.Contains(x.EdgeType))
                .Where(x => MatchesDirection(x, rootNodeId, query.Direction))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(take)
                .Select(CloneEdge)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>(edges);
    }

    public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return Task.FromResult(new ProjectionGraphSubgraph());

        var edgeTypes = NormalizeEdgeTypes(query.EdgeTypes);
        var depth = Math.Clamp(query.Depth, 1, 8);
        var take = Math.Clamp(query.Take, 1, 5000);

        var visitedNodeIds = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
        var collectedEdges = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);

        for (var currentDepth = 0; currentDepth < depth; currentDepth++)
        {
            if (frontier.Count == 0 || collectedEdges.Count >= take)
                break;

            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodeId in frontier)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<ProjectionGraphEdge> neighbors;
                lock (_gate)
                {
                    neighbors = _edges.Values
                        .Where(x => string.Equals(x.Scope, scope, StringComparison.Ordinal))
                        .Where(x => edgeTypes.Count == 0 || edgeTypes.Contains(x.EdgeType))
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

        List<ProjectionGraphNode> nodes;
        lock (_gate)
        {
            nodes = visitedNodeIds
                .Select(x =>
                {
                    var key = BuildNodeKey(scope, x);
                    if (_nodes.TryGetValue(key, out var existing))
                        return CloneNode(existing);

                    return new ProjectionGraphNode
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

        var graph = new ProjectionGraphSubgraph
        {
            Nodes = nodes,
            Edges = collectedEdges.Values.ToList(),
        };
        return Task.FromResult(graph);
    }

    private bool MatchesDirection(
        ProjectionGraphEdge edge,
        string rootNodeId,
        ProjectionGraphDirection direction)
    {
        return direction switch
        {
            ProjectionGraphDirection.Outbound => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal),
            ProjectionGraphDirection.Inbound => string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
            _ => string.Equals(edge.FromNodeId, rootNodeId, StringComparison.Ordinal) ||
                 string.Equals(edge.ToNodeId, rootNodeId, StringComparison.Ordinal),
        };
    }

    private static string ResolveCounterpartNodeId(ProjectionGraphEdge edge, string nodeId)
    {
        if (string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal))
            return edge.ToNodeId;
        if (string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal))
            return edge.FromNodeId;
        return "";
    }

    private static HashSet<string> NormalizeEdgeTypes(IReadOnlyList<string> edgeTypes)
    {
        return edgeTypes
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private string BuildNodeKey(string scope, string nodeId) => $"{scope}:{nodeId}";

    private string BuildEdgeKey(string scope, string edgeId) => $"{scope}:{edgeId}";

    private ProjectionGraphNode CloneNode(ProjectionGraphNode source) =>
        CloneNode(source, source.Scope, source.NodeId);

    private ProjectionGraphNode CloneNode(ProjectionGraphNode source, string scope, string nodeId)
    {
        var payload = JsonSerializer.Serialize(source, _jsonOptions);
        var clone = JsonSerializer.Deserialize<ProjectionGraphNode>(payload, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone graph node.");
        clone.Scope = scope;
        clone.NodeId = nodeId;
        return clone;
    }

    private ProjectionGraphEdge CloneEdge(ProjectionGraphEdge source) =>
        CloneEdge(source, source.Scope, source.EdgeId, source.FromNodeId, source.ToNodeId, source.EdgeType);

    private ProjectionGraphEdge CloneEdge(
        ProjectionGraphEdge source,
        string scope,
        string edgeId,
        string fromNodeId,
        string toNodeId,
        string relationType)
    {
        var payload = JsonSerializer.Serialize(source, _jsonOptions);
        var clone = JsonSerializer.Deserialize<ProjectionGraphEdge>(payload, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to clone graph edge.");
        clone.Scope = scope;
        clone.EdgeId = edgeId;
        clone.FromNodeId = fromNodeId;
        clone.ToNodeId = toNodeId;
        clone.EdgeType = relationType;
        return clone;
    }

    private static string NormalizeToken(string token) => token?.Trim() ?? "";
}
