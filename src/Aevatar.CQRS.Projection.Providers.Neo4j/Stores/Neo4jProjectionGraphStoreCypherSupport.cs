namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStoreCypherSupport
{
    internal static string BuildUpsertNodeCypher(string nodeLabel)
    {
        return $"MERGE (n:{nodeLabel} {{scope: $scope, nodeId: $nodeId}}) " +
               "SET n.nodeType = $nodeType, " +
               "n.propertiesJson = $propertiesJson, " +
               "n.updatedAtEpochMs = $updatedAtEpochMs, " +
               "n.projectionManaged = $projectionManaged, " +
               "n.projectionOwnerId = CASE WHEN $projectionOwnerId = '' THEN null ELSE $projectionOwnerId END";
    }

    internal static string BuildUpsertEdgeCypher(string nodeLabel, string edgeType)
    {
        return $"MERGE (from:{nodeLabel} {{scope: $scope, nodeId: $fromNodeId}}) " +
               "ON CREATE SET from.nodeType = 'Unknown', from.propertiesJson = '{}', from.updatedAtEpochMs = $updatedAtEpochMs " +
               $"MERGE (to:{nodeLabel} {{scope: $scope, nodeId: $toNodeId}}) " +
               "ON CREATE SET to.nodeType = 'Unknown', to.propertiesJson = '{}', to.updatedAtEpochMs = $updatedAtEpochMs " +
               $"MERGE (from)-[r:{edgeType} {{scope: $scope, edgeId: $edgeId}}]->(to) " +
               "SET r.relationType = $relationType, " +
               "r.propertiesJson = $propertiesJson, " +
               "r.updatedAtEpochMs = $updatedAtEpochMs, " +
               "r.projectionManaged = $projectionManaged, " +
               "r.projectionOwnerId = CASE WHEN $projectionOwnerId = '' THEN null ELSE $projectionOwnerId END";
    }

    internal static string BuildDeleteNodeCypher(string nodeLabel)
    {
        return $"MATCH (n:{nodeLabel} {{scope: $scope, nodeId: $nodeId}}) " +
               "WHERE NOT (n)-[]-() DELETE n";
    }

    internal static string BuildDeleteEdgeCypher(string edgeType)
    {
        return $"MATCH ()-[r:{edgeType} {{scope: $scope, edgeId: $edgeId}}]->() DELETE r";
    }

    internal static string BuildListNodesByOwnerCypher(string nodeLabel)
    {
        return $"MATCH (n:{nodeLabel} {{scope: $scope}}) " +
               "WHERE coalesce(n.projectionManaged, false) = true " +
               "AND n.projectionOwnerId = $ownerId " +
               "RETURN n.nodeId AS nodeId, " +
               "coalesce(n.nodeType, '') AS nodeType, " +
               "coalesce(n.propertiesJson, '{}') AS propertiesJson, " +
               "coalesce(n.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
               "ORDER BY updatedAtEpochMs DESC SKIP $skip LIMIT $take";
    }

    internal static string BuildListEdgesByOwnerCypher(string edgeType)
    {
        return $"MATCH ()-[r:{edgeType} {{scope: $scope}}]->() " +
               "WHERE coalesce(r.projectionManaged, false) = true " +
               "AND r.projectionOwnerId = $ownerId " +
               "RETURN r.edgeId AS edgeId, " +
               "startNode(r).nodeId AS fromNodeId, " +
               "endNode(r).nodeId AS toNodeId, " +
               "coalesce(r.relationType, '') AS relationType, " +
               "coalesce(r.propertiesJson, '{}') AS propertiesJson, " +
               "coalesce(r.updatedAtEpochMs, 0) AS updatedAtEpochMs " +
               "ORDER BY updatedAtEpochMs DESC SKIP $skip LIMIT $take";
    }

    internal static string BuildReplaceOwnerGraphCypher(string nodeLabel, string edgeType)
    {
        return $"OPTIONAL MATCH ()-[old:{edgeType} {{scope: $scope}}]->() " +
               "WHERE coalesce(old.projectionManaged, false) = true " +
               "AND old.projectionOwnerId = $ownerId " +
               "DELETE old " +
               "WITH $nodes AS nodes, $edges AS edges, $targetNodeIds AS targetNodeIds, $scope AS scope, $ownerId AS ownerId " +
               "FOREACH (node IN nodes | " +
               $"MERGE (n:{nodeLabel} {{scope: scope, nodeId: node.nodeId}}) " +
               "SET n.nodeType = node.nodeType, " +
               "n.propertiesJson = node.propertiesJson, " +
               "n.updatedAtEpochMs = node.updatedAtEpochMs, " +
               "n.projectionManaged = node.projectionManaged, " +
               "n.projectionOwnerId = CASE WHEN node.projectionOwnerId = '' THEN null ELSE node.projectionOwnerId END) " +
               "WITH edges, targetNodeIds, scope, ownerId " +
               "FOREACH (edge IN edges | " +
               $"MERGE (from:{nodeLabel} {{scope: scope, nodeId: edge.fromNodeId}}) " +
               "ON CREATE SET from.nodeType = 'Unknown', from.propertiesJson = '{}', from.updatedAtEpochMs = edge.updatedAtEpochMs " +
               $"MERGE (to:{nodeLabel} {{scope: scope, nodeId: edge.toNodeId}}) " +
               "ON CREATE SET to.nodeType = 'Unknown', to.propertiesJson = '{}', to.updatedAtEpochMs = edge.updatedAtEpochMs " +
               $"MERGE (from)-[r:{edgeType} {{scope: scope, edgeId: edge.edgeId}}]->(to) " +
               "SET r.relationType = edge.relationType, " +
               "r.propertiesJson = edge.propertiesJson, " +
               "r.updatedAtEpochMs = edge.updatedAtEpochMs, " +
               "r.projectionManaged = edge.projectionManaged, " +
               "r.projectionOwnerId = CASE WHEN edge.projectionOwnerId = '' THEN null ELSE edge.projectionOwnerId END) " +
               $"WITH targetNodeIds, scope, ownerId MATCH (n:{nodeLabel} {{scope: scope}}) " +
               "WHERE coalesce(n.projectionManaged, false) = true " +
               "AND n.projectionOwnerId = ownerId " +
               "AND NOT n.nodeId IN targetNodeIds " +
               $"AND NOT EXISTS {{ MATCH (n)-[managedRel:{edgeType}]-() WHERE managedRel.scope = scope }} " +
               "DELETE n";
    }

    internal static string BuildNeighborCypher(
        string nodeLabel,
        string edgeType,
        ProjectionGraphDirection direction)
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
                $"MATCH (root:{nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})-[r:{edgeType}]->(:{nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
            ProjectionGraphDirection.Inbound =>
                $"MATCH (root:{nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})<-[r:{edgeType}]-(:{nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
            _ =>
                $"MATCH (root:{nodeLabel} {{scope: $scope, nodeId: $rootNodeId}})-[r:{edgeType}]-(:{nodeLabel} {{scope: $scope}}) " +
                filter +
                projection,
        };
    }

    internal static string BuildSubgraphEdgesCypher(
        string nodeLabel,
        string edgeType,
        ProjectionGraphDirection direction,
        int depth)
    {
        var pathPattern = direction switch
        {
            ProjectionGraphDirection.Outbound =>
                $"(root)-[:{edgeType}*1..{depth}]->()",
            ProjectionGraphDirection.Inbound =>
                $"(root)<-[:{edgeType}*1..{depth}]-()",
            _ =>
                $"(root)-[:{edgeType}*1..{depth}]-()",
        };

        return $"MATCH (root:{nodeLabel} {{scope: $scope, nodeId: $rootNodeId}}) " +
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

    internal static string BuildGetNodesByIdsCypher(string nodeLabel)
    {
        return $"MATCH (n:{nodeLabel} {{scope: $scope}}) " +
               "WHERE n.nodeId IN $nodeIds " +
               "RETURN n.nodeId AS nodeId, " +
               "coalesce(n.nodeType, '') AS nodeType, " +
               "coalesce(n.propertiesJson, '{}') AS propertiesJson, " +
               "coalesce(n.updatedAtEpochMs, 0) AS updatedAtEpochMs";
    }

    internal static string BuildCreateNodeConstraintCypher(string nodeLabel, string constraintName)
    {
        return $"CREATE CONSTRAINT {constraintName} IF NOT EXISTS " +
               $"FOR (n:{nodeLabel}) REQUIRE (n.scope, n.nodeId) IS UNIQUE";
    }
}
