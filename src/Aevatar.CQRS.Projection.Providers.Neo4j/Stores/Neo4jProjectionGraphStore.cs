using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed class Neo4jProjectionGraphStore
    : IProjectionGraphStore,
      IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly string _scope;
    private readonly string _database;
    private readonly string _nodeLabel;
    private readonly string _edgeType;
    private readonly bool _autoCreateConstraints;
    private readonly int _maxTraversalDepth;
    private readonly string _providerName;
    private readonly ILogger<Neo4jProjectionGraphStore> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private bool _schemaInitialized;

    public Neo4jProjectionGraphStore(
        Neo4jProjectionGraphStoreOptions options,
        string scope,
        string providerName = ProjectionProviderNames.Neo4j,
        ILogger<Neo4jProjectionGraphStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        _scope = scope.Trim();
        _database = options.Database?.Trim() ?? "";
        _nodeLabel = NormalizeLabel(options.NodeLabel, "ProjectionGraphNode");
        _edgeType = NormalizeLabel(options.EdgeType, "PROJECTION_REL");
        _autoCreateConstraints = options.AutoCreateConstraints;
        _maxTraversalDepth = Math.Clamp(options.MaxTraversalDepth, 1, 8);
        _providerName = string.IsNullOrWhiteSpace(providerName)
            ? ProjectionProviderNames.Neo4j
            : providerName.Trim();
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
        var cypher = $"MERGE (n:{_nodeLabel} {{scope: $scope, nodeId: $nodeId}}) " +
                     "SET n.nodeType = $nodeType, n.propertiesJson = $propertiesJson, n.updatedAtEpochMs = $updatedAtEpochMs";
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["nodeId"] = nodeId,
            ["nodeType"] = nodeType,
            ["propertiesJson"] = propertiesJson,
            ["updatedAtEpochMs"] = updatedAtEpochMs,
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
        var cypher = $"MERGE (from:{_nodeLabel} {{scope: $scope, nodeId: $fromNodeId}}) " +
                     "ON CREATE SET from.nodeType = 'Unknown', from.propertiesJson = '{}', from.updatedAtEpochMs = $updatedAtEpochMs " +
                     $"MERGE (to:{_nodeLabel} {{scope: $scope, nodeId: $toNodeId}}) " +
                     "ON CREATE SET to.nodeType = 'Unknown', to.propertiesJson = '{}', to.updatedAtEpochMs = $updatedAtEpochMs " +
                     $"MERGE (from)-[r:{_edgeType} {{scope: $scope, edgeId: $edgeId}}]->(to) " +
                     "SET r.relationType = $relationType, r.propertiesJson = $propertiesJson, r.updatedAtEpochMs = $updatedAtEpochMs";
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["edgeId"] = edgeId,
            ["fromNodeId"] = fromNodeId,
            ["toNodeId"] = toNodeId,
            ["relationType"] = relationType,
            ["propertiesJson"] = propertiesJson,
            ["updatedAtEpochMs"] = updatedAtEpochMs,
        };

        await EnsureSchemaAsync(ct);
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

        var depth = Math.Clamp(query.Depth, 1, _maxTraversalDepth);
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
                var neighbors = await GetNeighborsAsync(
                    new ProjectionGraphQuery
                    {
                        Scope = scope,
                        RootNodeId = nodeId,
                        Direction = query.Direction,
                        EdgeTypes = query.EdgeTypes,
                        Depth = 1,
                        Take = take - collectedEdges.Count,
                    },
                    ct);

                foreach (var edge in neighbors)
                {
                    if (collectedEdges.Count >= take)
                        break;

                    if (!collectedEdges.ContainsKey(edge.EdgeId))
                        collectedEdges[edge.EdgeId] = edge;

                    var counterpartNodeId = ResolveCounterpartNodeId(edge, nodeId);
                    if (counterpartNodeId.Length == 0)
                        continue;

                    if (visitedNodeIds.Add(counterpartNodeId))
                        nextFrontier.Add(counterpartNodeId);
                }
            }

            frontier = nextFrontier;
        }

        var nodes = await GetNodesByIdsAsync(scope, visitedNodeIds, ct);
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
            Edges = collectedEdges.Values.ToList(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _schemaLock.Dispose();
        await _driver.DisposeAsync();
    }

    private async Task<List<ProjectionGraphNode>> GetNodesByIdsAsync(
        string scope,
        IReadOnlySet<string> nodeIds,
        CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var cypher = $"MATCH (n:{_nodeLabel} {{scope: $scope}}) " +
                     "WHERE n.nodeId IN $nodeIds " +
                     "RETURN n.nodeId AS nodeId, " +
                     "coalesce(n.nodeType, '') AS nodeType, " +
                     "coalesce(n.propertiesJson, '{}') AS propertiesJson, " +
                     "coalesce(n.updatedAtEpochMs, 0) AS updatedAtEpochMs";
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["nodeIds"] = nodeIds.ToArray(),
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var nodes = new List<ProjectionGraphNode>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("nodeId", out var nodeIdValue))
                continue;
            var nodeId = NormalizeToken(nodeIdValue.As<string>());
            if (nodeId.Length == 0)
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
                Scope = scope,
                NodeId = nodeId,
                NodeType = nodeType,
                Properties = DeserializeProperties(propertiesJson),
                UpdatedAt = FromUnixTimeMilliseconds(updatedAtEpochMs),
            });
        }

        return nodes;
    }

    private ProjectionGraphEdge? BuildEdgeFromRow(string scope, IReadOnlyDictionary<string, object> row)
    {
        if (!row.TryGetValue("edgeId", out var edgeIdValue))
            return null;
        if (!row.TryGetValue("fromNodeId", out var fromNodeIdValue))
            return null;
        if (!row.TryGetValue("toNodeId", out var toNodeIdValue))
            return null;

        var edgeId = NormalizeToken(edgeIdValue.As<string>());
        var fromNodeId = NormalizeToken(fromNodeIdValue.As<string>());
        var toNodeId = NormalizeToken(toNodeIdValue.As<string>());
        if (edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0)
            return null;

        var relationType = row.TryGetValue("relationType", out var relationTypeValue)
            ? NormalizeToken(relationTypeValue.As<string>())
            : "Unknown";
        if (relationType.Length == 0)
            relationType = "Unknown";
        var propertiesJson = row.TryGetValue("propertiesJson", out var propertiesJsonValue)
            ? propertiesJsonValue.As<string>()
            : "{}";
        var updatedAtEpochMs = row.TryGetValue("updatedAtEpochMs", out var updatedAtEpochMsValue)
            ? updatedAtEpochMsValue.As<long>()
            : 0L;

        return new ProjectionGraphEdge
        {
            Scope = scope,
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            EdgeType = relationType,
            Properties = DeserializeProperties(propertiesJson),
            UpdatedAt = FromUnixTimeMilliseconds(updatedAtEpochMs),
        };
    }

    private string BuildNeighborCypher(ProjectionGraphDirection direction, int take)
    {
        var filter = "WHERE size($edgeTypes) = 0 OR r.relationType IN $edgeTypes ";
        var projection = "RETURN r.edgeId AS edgeId, " +
                         "startNode(r).nodeId AS fromNodeId, " +
                         "endNode(r).nodeId AS toNodeId, " +
                         "coalesce(r.relationType, '') AS relationType, " +
                         "coalesce(r.propertiesJson, '{}') AS propertiesJson, " +
                         "coalesce(r.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
                         "ORDER BY updatedAtEpochMs DESC LIMIT $take";
        return direction switch
        {
            ProjectionGraphDirection.Outbound =>
                $"MATCH (root:{_nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})-[r:{_edgeType}]->(:{_nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
            ProjectionGraphDirection.Inbound =>
                $"MATCH (root:{_nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})<-[r:{_edgeType}]-(:{_nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
            _ =>
                $"MATCH (root:{_nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})-[r:{_edgeType}]-(:{_nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
        };
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (!_autoCreateConstraints || _schemaInitialized)
            return;

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaInitialized)
                return;

            var nodeConstraintName = NormalizeConstraintName($"projection_graph_node_scope_id_{_nodeLabel}");
            var cypher = $"CREATE CONSTRAINT {nodeConstraintName} IF NOT EXISTS " +
                         $"FOR (n:{_nodeLabel}) REQUIRE (n.scope, n.nodeId) IS UNIQUE";
            await ExecuteWriteAsync(cypher, new Dictionary<string, object?>(), ct);
            _schemaInitialized = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task ExecuteWriteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Write);
        var cursor = await session.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        ct.ThrowIfCancellationRequested();
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ExecuteReadAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Read);
        var cursor = await session.RunAsync(cypher, parameters);
        var rows = await cursor.ToListAsync(record =>
            (IReadOnlyDictionary<string, object>)record.Values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        ct.ThrowIfCancellationRequested();
        return rows;
    }

    private IAsyncSession CreateSession(AccessMode accessMode)
    {
        return _driver.AsyncSession(options =>
        {
            options.WithDefaultAccessMode(accessMode);
            if (_database.Length > 0)
                options.WithDatabase(_database);
        });
    }

    private static string ResolveCounterpartNodeId(ProjectionGraphEdge edge, string nodeId)
    {
        if (string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal))
            return edge.ToNodeId;
        if (string.Equals(edge.ToNodeId, nodeId, StringComparison.Ordinal))
            return edge.FromNodeId;
        return "";
    }

    private static string[] NormalizeEdgeTypes(IReadOnlyList<string> edgeTypes)
    {
        return edgeTypes
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private string SerializeProperties(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.Count == 0)
            return "{}";
        return JsonSerializer.Serialize(properties, _jsonOptions);
    }

    private Dictionary<string, string> DeserializeProperties(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(payload, _jsonOptions);
            return parsed == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize graph edge properties payload. provider={Provider} scope={Scope}",
                _providerName,
                _scope);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static long NormalizeTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp == default)
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return timestamp.ToUnixTimeMilliseconds();
    }

    private static DateTimeOffset FromUnixTimeMilliseconds(long value)
    {
        var safeValue = Math.Max(0, value);
        return DateTimeOffset.FromUnixTimeMilliseconds(safeValue);
    }

    private static string NormalizeToken(string token) => token?.Trim() ?? "";

    private static string NormalizeLabel(string rawLabel, string fallback)
    {
        var label = (rawLabel ?? "").Trim();
        if (label.Length == 0)
            label = fallback;

        var chars = label
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
            .ToArray();
        var normalized = new string(chars);
        if (normalized.Length == 0)
            normalized = fallback;
        if (char.IsDigit(normalized[0]))
            normalized = $"N_{normalized}";
        return normalized;
    }

    private static string NormalizeConstraintName(string rawName)
    {
        var chars = rawName
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = new string(chars);
        if (normalized.Length == 0)
            return "projection_graph_constraint";
        if (char.IsDigit(normalized[0]))
            normalized = $"c_{normalized}";
        return normalized;
    }
}
