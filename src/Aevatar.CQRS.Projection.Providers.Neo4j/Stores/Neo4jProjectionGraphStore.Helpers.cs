using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed partial class Neo4jProjectionGraphStore
{
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

    private string BuildSubgraphEdgesCypher(ProjectionGraphDirection direction, int depth)
    {
        var boundedDepth = Math.Clamp(depth, 1, _maxTraversalDepth);
        var pathPattern = direction switch
        {
            ProjectionGraphDirection.Outbound =>
                $"(root)-[:{_edgeType}*1..{boundedDepth}]->()",
            ProjectionGraphDirection.Inbound =>
                $"(root)<-[:{_edgeType}*1..{boundedDepth}]-()",
            _ =>
                $"(root)-[:{_edgeType}*1..{boundedDepth}]-()",
        };
        return $"MATCH (root:{_nodeLabel} {{scope: $scope, nodeId: $rootNodeId}}) " +
               $"OPTIONAL MATCH p={pathPattern} " +
               "WHERE p IS NULL OR (" +
               "all(n IN nodes(p) WHERE coalesce(n.scope, '') = $scope) " +
               "AND (size($edgeTypes) = 0 OR all(rel IN relationships(p) WHERE rel.relationType IN $edgeTypes))) " +
               "UNWIND CASE WHEN p IS NULL THEN [] ELSE relationships(p) END AS r " +
               "WITH DISTINCT r " +
               "RETURN r.edgeId AS edgeId, " +
               "startNode(r).nodeId AS fromNodeId, " +
               "endNode(r).nodeId AS toNodeId, " +
               "coalesce(r.relationType, '') AS relationType, " +
               "coalesce(r.propertiesJson, '{}') AS propertiesJson, " +
               "coalesce(r.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
               "ORDER BY updatedAtEpochMs DESC LIMIT $take";
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

    private static string[] NormalizeEdgeTypes(IReadOnlyList<string> edgeTypes)
    {
        return edgeTypes
            .Select(NormalizeToken)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ResolveProjectionManaged(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedMarkerKey, out var markerValue))
            return false;

        var normalizedMarker = NormalizeToken(markerValue);
        return string.Equals(
            normalizedMarker,
            ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
            StringComparison.Ordinal);
    }

    private static string ResolveProjectionOwnerId(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue(ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey, out var ownerId))
            return "";

        return NormalizeToken(ownerId);
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
                "Failed to deserialize graph properties payload. provider={Provider}",
                ProviderName);
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
