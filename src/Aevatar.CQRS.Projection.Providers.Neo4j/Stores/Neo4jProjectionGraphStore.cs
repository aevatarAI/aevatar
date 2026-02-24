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
        _nodeLabel = NormalizeLabel(options.NodeLabel, "ProjectionGraphNode");
        _edgeType = NormalizeLabel(options.EdgeType, "PROJECTION_REL");
        _autoCreateConstraints = options.AutoCreateConstraints;
        _maxTraversalDepth = Math.Clamp(options.MaxTraversalDepth, 1, 8);
        _logger = logger ?? NullLogger<Neo4jProjectionGraphStore>.Instance;

        var auth = string.IsNullOrWhiteSpace(options.Username)
            ? AuthTokens.None
            : AuthTokens.Basic(options.Username.Trim(), options.Password ?? "");
        _driver = GraphDatabase.Driver(options.Uri, auth, config =>
            config.WithConnectionTimeout(TimeSpan.FromMilliseconds(Math.Max(1000, options.RequestTimeoutMs))));
    }

    public async Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();

        var scope = NormalizeToken(node.Scope);
        var nodeId = NormalizeToken(node.NodeId);
        if (scope.Length == 0 || nodeId.Length == 0)
            throw new InvalidOperationException("Graph node requires non-empty scope and nodeId.");

        var nodeType = NormalizeToken(node.NodeType);
        if (nodeType.Length == 0)
            nodeType = "Unknown";
        var updatedAtEpochMs = NormalizeTimestamp(node.UpdatedAt);
        var propertiesJson = SerializeProperties(node.Properties);
        var projectionManaged = ResolveProjectionManaged(node.Properties);
        var projectionOwnerId = ResolveProjectionOwnerId(node.Properties);
        var cypher = $"MERGE (n:{_nodeLabel} {{scope: $scope, nodeId: $nodeId}}) " +
                     "SET n.nodeType = $nodeType, " +
                     "n.propertiesJson = $propertiesJson, " +
                     "n.updatedAtEpochMs = $updatedAtEpochMs, " +
                     "n.projectionManaged = $projectionManaged, " +
                     "n.projectionOwnerId = CASE WHEN $projectionOwnerId = '' THEN null ELSE $projectionOwnerId END";
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

        var scope = NormalizeToken(edge.Scope);
        var edgeId = NormalizeToken(edge.EdgeId);
        var fromNodeId = NormalizeToken(edge.FromNodeId);
        var toNodeId = NormalizeToken(edge.ToNodeId);
        var relationType = NormalizeToken(edge.EdgeType);
        if (scope.Length == 0 || edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0 || relationType.Length == 0)
        {
            throw new InvalidOperationException("Graph edge requires non-empty scope/edgeId/fromNodeId/toNodeId/relationType.");
        }

        var updatedAtEpochMs = NormalizeTimestamp(edge.UpdatedAt);
        var propertiesJson = SerializeProperties(edge.Properties);
        var projectionManaged = ResolveProjectionManaged(edge.Properties);
        var projectionOwnerId = ResolveProjectionOwnerId(edge.Properties);
        var cypher = $"MERGE (from:{_nodeLabel} {{scope: $scope, nodeId: $fromNodeId}}) " +
                     "ON CREATE SET from.nodeType = 'Unknown', from.propertiesJson = '{}', from.updatedAtEpochMs = $updatedAtEpochMs " +
                     $"MERGE (to:{_nodeLabel} {{scope: $scope, nodeId: $toNodeId}}) " +
                     "ON CREATE SET to.nodeType = 'Unknown', to.propertiesJson = '{}', to.updatedAtEpochMs = $updatedAtEpochMs " +
                     $"MERGE (from)-[r:{_edgeType} {{scope: $scope, edgeId: $edgeId}}]->(to) " +
                     "SET r.relationType = $relationType, " +
                     "r.propertiesJson = $propertiesJson, " +
                     "r.updatedAtEpochMs = $updatedAtEpochMs, " +
                     "r.projectionManaged = $projectionManaged, " +
                     "r.projectionOwnerId = CASE WHEN $projectionOwnerId = '' THEN null ELSE $projectionOwnerId END";
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
        var scopeValue = NormalizeToken(scope);
        var nodeIdValue = NormalizeToken(nodeId);
        if (scopeValue.Length == 0 || nodeIdValue.Length == 0)
            return;

        await EnsureSchemaAsync(ct);
        var cypher = $"MATCH (n:{_nodeLabel} {{scope: $scope, nodeId: $nodeId}}) " +
                     "WHERE NOT (n)-[]-() DELETE n";
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
        var scopeValue = NormalizeToken(scope);
        var edgeIdValue = NormalizeToken(edgeId);
        if (scopeValue.Length == 0 || edgeIdValue.Length == 0)
            return;

        await EnsureSchemaAsync(ct);
        var cypher = $"MATCH ()-[r:{_edgeType} {{scope: $scope, edgeId: $edgeId}}]->() DELETE r";
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
        var scopeValue = NormalizeToken(scope);
        var ownerValue = NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(take, 1, 50000);
        var boundedSkip = Math.Max(0, skip);
        var cypher = $"MATCH (n:{_nodeLabel} {{scope: $scope}}) " +
                     "WHERE coalesce(n.projectionManaged, false) = true " +
                     "AND n.projectionOwnerId = $ownerId " +
                     "RETURN n.nodeId AS nodeId, " +
                     "coalesce(n.nodeType, '') AS nodeType, " +
                     "coalesce(n.propertiesJson, '{}') AS propertiesJson, " +
                     "coalesce(n.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
                     "ORDER BY updatedAtEpochMs DESC SKIP $skip LIMIT $take";
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
            if (!row.TryGetValue("nodeId", out var nodeIdValue))
                continue;

            var resolvedNodeId = NormalizeToken(nodeIdValue.As<string>());
            if (resolvedNodeId.Length == 0)
                continue;

            var nodeType = row.TryGetValue("nodeType", out var nodeTypeValue)
                ? NormalizeToken(nodeTypeValue.As<string>())
                : "Unknown";
            if (nodeType.Length == 0)
                nodeType = "Unknown";

            var propertiesJson = row.TryGetValue("propertiesJson", out var propertiesJsonValue)
                ? propertiesJsonValue.As<string>()
                : "{}";
            var updatedAtEpochMs = row.TryGetValue("updatedAtEpochMs", out var updatedAtEpochMsValue)
                ? updatedAtEpochMsValue.As<long>()
                : 0L;

            nodes.Add(new ProjectionGraphNode
            {
                Scope = scopeValue,
                NodeId = resolvedNodeId,
                NodeType = nodeType,
                Properties = DeserializeProperties(propertiesJson),
                UpdatedAt = FromUnixTimeMilliseconds(updatedAtEpochMs),
            });
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
        var scopeValue = NormalizeToken(scope);
        var ownerValue = NormalizeToken(ownerId);
        if (scopeValue.Length == 0 || ownerValue.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(take, 1, 50000);
        var boundedSkip = Math.Max(0, skip);
        var cypher = $"MATCH ()-[r:{_edgeType} {{scope: $scope}}]->() " +
                     "WHERE coalesce(r.projectionManaged, false) = true " +
                     "AND r.projectionOwnerId = $ownerId " +
                     "RETURN r.edgeId AS edgeId, " +
                     "startNode(r).nodeId AS fromNodeId, " +
                     "endNode(r).nodeId AS toNodeId, " +
                     "coalesce(r.relationType, '') AS relationType, " +
                     "coalesce(r.propertiesJson, '{}') AS propertiesJson, " +
                     "coalesce(r.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
                     "ORDER BY updatedAtEpochMs DESC SKIP $skip LIMIT $take";
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
            var edge = BuildEdgeFromRow(scopeValue, row);
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
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(query.Take, 1, 5000);
        var edgeTypes = NormalizeEdgeTypes(query.EdgeTypes);
        var cypher = BuildNeighborCypher(query.Direction, boundedTake);
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
            var edge = BuildEdgeFromRow(scope, row);
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
        var scope = NormalizeToken(query.Scope);
        var rootNodeId = NormalizeToken(query.RootNodeId);
        if (scope.Length == 0 || rootNodeId.Length == 0)
            return new ProjectionGraphSubgraph();

        await EnsureSchemaAsync(ct);
        var depth = Math.Clamp(query.Depth, 1, _maxTraversalDepth);
        var take = Math.Clamp(query.Take, 1, 5000);
        var edgeTypes = NormalizeEdgeTypes(query.EdgeTypes);
        var cypher = BuildSubgraphEdgesCypher(query.Direction, depth);
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
            var edge = BuildEdgeFromRow(scope, row);
            if (edge != null)
                edges.Add(edge);
        }

        var nodeIds = edges
            .SelectMany(x => new[] { x.FromNodeId, x.ToNodeId })
            .Append(rootNodeId)
            .Where(x => NormalizeToken(x).Length > 0)
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

}
