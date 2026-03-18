using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed partial class Neo4jProjectionGraphStore
    : IProjectionGraphStore,
      IAsyncDisposable
{
    private const string ProviderName = "Neo4j";
    private readonly IDriver _driver;
    private readonly string _database;
    private readonly string _nodeLabel;
    private readonly string _edgeType;
    private readonly bool _autoCreateConstraints;
    private readonly int _maxTraversalDepth;
    private readonly ILogger<Neo4jProjectionGraphStore> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private bool _schemaInitialized;

    public Neo4jProjectionGraphStore(
        Neo4jProjectionGraphStoreOptions options,
        ILogger<Neo4jProjectionGraphStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _database = options.Database?.Trim() ?? "";
        _nodeLabel = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeLabel(
            options.NodeLabel,
            "ProjectionGraphNode");
        _edgeType = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeLabel(
            options.EdgeType,
            "PROJECTION_REL");
        _autoCreateConstraints = options.AutoCreateConstraints;
        _maxTraversalDepth = Math.Clamp(options.MaxTraversalDepth, 1, 8);
        _logger = logger ?? NullLogger<Neo4jProjectionGraphStore>.Instance;

        var auth = string.IsNullOrWhiteSpace(options.Username)
            ? AuthTokens.None
            : AuthTokens.Basic(options.Username.Trim(), options.Password ?? "");
        _driver = GraphDatabase.Driver(options.Uri, auth, config =>
            config.WithConnectionTimeout(TimeSpan.FromMilliseconds(Math.Max(1000, options.RequestTimeoutMs))));
    }

    public async Task ReplaceOwnerGraphAsync(
        ProjectionOwnedGraph graph,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ct.ThrowIfCancellationRequested();

        var scope = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(graph.Scope);
        var ownerId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(graph.OwnerId);
        if (scope.Length == 0 || ownerId.Length == 0)
            throw new InvalidOperationException("Owned graph requires non-empty scope and ownerId.");

        var nodes = graph.Nodes
            .Select(node => new Dictionary<string, object?>
            {
                ["nodeId"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(node.NodeId),
                ["nodeType"] = NormalizeNodeType(node.NodeType),
                ["propertiesJson"] = SerializeProperties(node.Properties),
                ["updatedAtEpochMs"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeTimestamp(node.UpdatedAt),
                ["projectionManaged"] = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionManaged(node.Properties),
                ["projectionOwnerId"] = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionOwnerId(node.Properties),
            })
            .Where(node => ((string?)node["nodeId"])?.Length > 0)
            .ToArray();
        var edges = graph.Edges
            .Select(edge => new Dictionary<string, object?>
            {
                ["edgeId"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.EdgeId),
                ["fromNodeId"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.FromNodeId),
                ["toNodeId"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.ToNodeId),
                ["relationType"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.EdgeType),
                ["propertiesJson"] = SerializeProperties(edge.Properties),
                ["updatedAtEpochMs"] = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeTimestamp(edge.UpdatedAt),
                ["projectionManaged"] = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionManaged(edge.Properties),
                ["projectionOwnerId"] = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionOwnerId(edge.Properties),
            })
            .Where(edge =>
                ((string?)edge["edgeId"])?.Length > 0 &&
                ((string?)edge["fromNodeId"])?.Length > 0 &&
                ((string?)edge["toNodeId"])?.Length > 0 &&
                ((string?)edge["relationType"])?.Length > 0)
            .ToArray();

        await EnsureSchemaAsync(ct);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildReplaceOwnerGraphCypher(_nodeLabel, _edgeType);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["ownerId"] = ownerId,
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["targetNodeIds"] = nodes.Select(x => x["nodeId"]).OfType<string>().ToArray(),
        };
        await ExecuteWriteTransactionAsync(cypher, parameters, ct);
    }

    public async Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();

        var scope = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(node.Scope);
        var nodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(node.NodeId);
        if (scope.Length == 0 || nodeId.Length == 0)
            throw new InvalidOperationException("Graph node requires non-empty scope and nodeId.");

        var nodeType = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(node.NodeType);
        if (nodeType.Length == 0)
            nodeType = "Unknown";
        var updatedAtEpochMs = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeTimestamp(node.UpdatedAt);
        var propertiesJson = SerializeProperties(node.Properties);
        var projectionManaged = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionManaged(node.Properties);
        var projectionOwnerId = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionOwnerId(node.Properties);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildUpsertNodeCypher(_nodeLabel);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["nodeId"] = nodeId,
            ["nodeType"] = nodeType,
            ["propertiesJson"] = propertiesJson,
            ["updatedAtEpochMs"] = updatedAtEpochMs,
            ["projectionManaged"] = projectionManaged,
            ["projectionOwnerId"] = projectionOwnerId,
        };

        await EnsureSchemaAsync(ct);
        await ExecuteWriteAsync(cypher, parameters, ct);
    }

    public async Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();

        var scope = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.Scope);
        var edgeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.EdgeId);
        var fromNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.FromNodeId);
        var toNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.ToNodeId);
        var relationType = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edge.EdgeType);
        if (scope.Length == 0 || edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0 || relationType.Length == 0)
        {
            throw new InvalidOperationException("Graph edge requires non-empty scope/edgeId/fromNodeId/toNodeId/relationType.");
        }

        var updatedAtEpochMs = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeTimestamp(edge.UpdatedAt);
        var propertiesJson = SerializeProperties(edge.Properties);
        var projectionManaged = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionManaged(edge.Properties);
        var projectionOwnerId = Neo4jProjectionGraphStoreNormalizationSupport.ResolveProjectionOwnerId(edge.Properties);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildUpsertEdgeCypher(_nodeLabel, _edgeType);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["edgeId"] = edgeId,
            ["fromNodeId"] = fromNodeId,
            ["toNodeId"] = toNodeId,
            ["relationType"] = relationType,
            ["propertiesJson"] = propertiesJson,
            ["updatedAtEpochMs"] = updatedAtEpochMs,
            ["projectionManaged"] = projectionManaged,
            ["projectionOwnerId"] = projectionOwnerId,
        };

        await EnsureSchemaAsync(ct);
        await ExecuteWriteAsync(cypher, parameters, ct);
    }

    public async Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(scope);
        var nodeIdValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(nodeId);
        if (scopeValue.Length == 0 || nodeIdValue.Length == 0)
            return;

        await EnsureSchemaAsync(ct);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildDeleteNodeCypher(_nodeLabel);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scopeValue,
            ["nodeId"] = nodeIdValue,
        };
        await ExecuteWriteAsync(cypher, parameters, ct);
    }

    public async Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(scope);
        var edgeIdValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edgeId);
        if (scopeValue.Length == 0 || edgeIdValue.Length == 0)
            return;

        await EnsureSchemaAsync(ct);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildDeleteEdgeCypher(_edgeType);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scopeValue,
            ["edgeId"] = edgeIdValue,
        };
        await ExecuteWriteAsync(cypher, parameters, ct);
    }

    public async Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
        string scope,
        string ownerId,
        int skip = 0,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(scope);
        var ownerValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(take, 1, 50000);
        var boundedSkip = Math.Max(0, skip);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildListNodesByOwnerCypher(_nodeLabel);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scopeValue,
            ["ownerId"] = ownerValue,
            ["skip"] = boundedSkip,
            ["take"] = boundedTake,
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var nodes = new List<ProjectionGraphNode>(rows.Count);
        foreach (var row in rows)
        {
            var node = Neo4jProjectionGraphStoreRowMapper.MapNode(scopeValue, row, DeserializeProperties);
            if (node != null)
                nodes.Add(node);
        }

        return nodes;
    }

    public async Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
        string scope,
        string ownerId,
        int skip = 0,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopeValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(scope);
        var ownerValue = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(take, 1, 50000);
        var boundedSkip = Math.Max(0, skip);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildListEdgesByOwnerCypher(_edgeType);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scopeValue,
            ["ownerId"] = ownerValue,
            ["skip"] = boundedSkip,
            ["take"] = boundedTake,
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var edges = new List<ProjectionGraphEdge>(rows.Count);
        foreach (var row in rows)
        {
            var edge = Neo4jProjectionGraphStoreRowMapper.MapEdge(scopeValue, row, DeserializeProperties);
            if (edge != null)
                edges.Add(edge);
        }

        return edges;
    }

    public async Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(query.Scope);
        var rootNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(query.Take, 1, 5000);
        var edgeTypes = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeEdgeTypes(query.EdgeTypes);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildNeighborCypher(
            _nodeLabel,
            _edgeType,
            query.Direction);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["rootNodeId"] = rootNodeId,
            ["edgeTypes"] = edgeTypes,
            ["take"] = boundedTake,
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var edges = new List<ProjectionGraphEdge>(rows.Count);
        foreach (var row in rows)
        {
            var edge = Neo4jProjectionGraphStoreRowMapper.MapEdge(scope, row, DeserializeProperties);
            if (edge != null)
                edges.Add(edge);
        }

        return edges;
    }

    public async Task<ProjectionGraphSubgraph> GetSubgraphAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var scope = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(query.Scope);
        var rootNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return new ProjectionGraphSubgraph();

        await EnsureSchemaAsync(ct);
        var depth = Math.Clamp(query.Depth, 1, _maxTraversalDepth);
        var take = Math.Clamp(query.Take, 1, 5000);
        var edgeTypes = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeEdgeTypes(query.EdgeTypes);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildSubgraphEdgesCypher(
            _nodeLabel,
            _edgeType,
            query.Direction,
            depth);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["rootNodeId"] = rootNodeId,
            ["edgeTypes"] = edgeTypes,
            ["take"] = take,
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var edges = new List<ProjectionGraphEdge>(rows.Count);
        foreach (var row in rows)
        {
            var edge = Neo4jProjectionGraphStoreRowMapper.MapEdge(scope, row, DeserializeProperties);
            if (edge != null)
                edges.Add(edge);
        }

        var nodeIds = edges
            .SelectMany(x => new[] { x.FromNodeId, x.ToNodeId })
            .Append(rootNodeId)
            .Where(x => Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(x).Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var nodes = await GetNodesByIdsAsync(scope, nodeIds, ct);
        if (!nodes.Any(x => string.Equals(x.NodeId, rootNodeId, StringComparison.Ordinal)))
        {
            nodes.Add(new ProjectionGraphNode
            {
                Scope = scope,
                NodeId = rootNodeId,
                NodeType = "Unknown",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        return new ProjectionGraphSubgraph
        {
            Nodes = nodes,
            Edges = edges,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _schemaLock.Dispose();
        await _driver.DisposeAsync();
    }

    private static string NormalizeNodeType(string? value)
    {
        var normalized = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(value ?? string.Empty);
        return normalized.Length == 0 ? "Unknown" : normalized;
    }

}
