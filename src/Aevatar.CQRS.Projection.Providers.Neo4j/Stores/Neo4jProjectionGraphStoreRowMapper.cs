using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStoreRowMapper
{
    internal static ProjectionGraphEdge? MapEdge(
        string scope,
        IReadOnlyDictionary<string, object> row,
        Func<string, Dictionary<string, string>> deserializeProperties)
    {
        if (!row.TryGetValue("edgeId", out var edgeIdValue))
            return null;
        if (!row.TryGetValue("fromNodeId", out var fromNodeIdValue))
            return null;
        if (!row.TryGetValue("toNodeId", out var toNodeIdValue))
            return null;

        var edgeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(edgeIdValue.As<string>());
        var fromNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(fromNodeIdValue.As<string>());
        var toNodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(toNodeIdValue.As<string>());
        if (edgeId.Length == 0 || fromNodeId.Length == 0 || toNodeId.Length == 0)
            return null;

        var relationType = row.TryGetValue("relationType", out var relationTypeValue)
            ? Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(relationTypeValue.As<string>())
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
            Properties = deserializeProperties(propertiesJson),
            UpdatedAt = Neo4jProjectionGraphStoreNormalizationSupport.FromUnixTimeMilliseconds(updatedAtEpochMs),
        };
    }

    internal static ProjectionGraphNode? MapNode(
        string scope,
        IReadOnlyDictionary<string, object> row,
        Func<string, Dictionary<string, string>> deserializeProperties)
    {
        if (!row.TryGetValue("nodeId", out var nodeIdValue))
            return null;

        var nodeId = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(nodeIdValue.As<string>());
        if (nodeId.Length == 0)
            return null;

        var nodeType = row.TryGetValue("nodeType", out var nodeTypeValue)
            ? Neo4jProjectionGraphStoreNormalizationSupport.NormalizeToken(nodeTypeValue.As<string>())
            : "Unknown";
        if (nodeType.Length == 0)
            nodeType = "Unknown";

        var propertiesJson = row.TryGetValue("propertiesJson", out var propertiesJsonValue)
            ? propertiesJsonValue.As<string>()
            : "{}";
        var updatedAtEpochMs = row.TryGetValue("updatedAtEpochMs", out var updatedAtEpochMsValue)
            ? updatedAtEpochMsValue.As<long>()
            : 0L;

        return new ProjectionGraphNode
        {
            Scope = scope,
            NodeId = nodeId,
            NodeType = nodeType,
            Properties = deserializeProperties(propertiesJson),
            UpdatedAt = Neo4jProjectionGraphStoreNormalizationSupport.FromUnixTimeMilliseconds(updatedAtEpochMs),
        };
    }
}
