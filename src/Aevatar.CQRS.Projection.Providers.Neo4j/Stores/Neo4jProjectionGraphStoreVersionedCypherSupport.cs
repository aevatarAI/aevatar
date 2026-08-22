namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

internal static class Neo4jProjectionGraphStoreVersionedCypherSupport
{
    internal static string BuildCreateOwnerStateConstraintCypher(string label, string constraintName) =>
        $"CREATE CONSTRAINT {constraintName} IF NOT EXISTS " +
        $"FOR (n:{label}) REQUIRE (n.physicalNamespace, n.projectionOwnerId) IS UNIQUE";

    internal static string BuildCreateEventConstraintCypher(string label, string constraintName) =>
        $"CREATE CONSTRAINT {constraintName} IF NOT EXISTS " +
        $"FOR (n:{label}) REQUIRE (n.physicalNamespace, n.projectionOwnerId, n.eventId) IS UNIQUE";

    internal static string BuildCreateEdgeIdentityConstraintCypher(string label, string constraintName) =>
        $"CREATE CONSTRAINT {constraintName} IF NOT EXISTS " +
        $"FOR (n:{label}) REQUIRE (n.physicalNamespace, n.edgeId) IS UNIQUE";

    /// <summary>
    /// Owner seek for edge identities: the repair/cutover stale-element read and the owner
    /// snapshot read match identities by (physicalNamespace, projectionOwnerId) alone; without
    /// this index the planner can only prefix-scan the pending (…, status, fromNodeId) index or
    /// scan the label.
    /// </summary>
    internal static string BuildCreateEdgeIdentityOwnerIndexCypher(string label, string indexName) =>
        $"CREATE RANGE INDEX {indexName} IF NOT EXISTS " +
        $"FOR (n:{label}) ON (n.physicalNamespace, n.projectionOwnerId)";

    internal static string BuildCreatePendingFromIndexCypher(string label, string indexName) =>
        $"CREATE RANGE INDEX {indexName} IF NOT EXISTS " +
        $"FOR (n:{label}) ON (n.physicalNamespace, n.projectionOwnerId, n.status, n.fromNodeId)";

    internal static string BuildCreatePendingToIndexCypher(string label, string indexName) =>
        $"CREATE RANGE INDEX {indexName} IF NOT EXISTS " +
        $"FOR (n:{label}) ON (n.physicalNamespace, n.projectionOwnerId, n.status, n.toNodeId)";

    /// <summary>
    /// Every incremental delta reads, deletes and rewires live relationships by exact edge id
    /// inside one physical namespace. Without this index the planner can only prefix-scan the
    /// (scope, projectionOwnerId) relationship index across the whole namespace, which is the
    /// same unrelated-owner scan that #3473 removed from the legacy replacement path.
    /// </summary>
    internal static string BuildCreateRelationshipEdgeIdIndexCypher(string edgeType, string indexName) =>
        $"CREATE RANGE INDEX {indexName} IF NOT EXISTS " +
        $"FOR ()-[r:{edgeType}]-() ON (r.scope, r.edgeId)";

    internal static string BuildLockOwnerStateCypher(string ownerStateLabel) =>
        $"MERGE (state:{ownerStateLabel} {{physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "ON CREATE SET state.initialized = false, state.lockToken = 0 " +
        "SET state.lockToken = coalesce(state.lockToken, 0) + 1 " +
        "RETURN coalesce(state.initialized, false) AS initialized, " +
        "coalesce(state.projectionKind, '') AS projectionKind, " +
        "coalesce(state.logicalScope, '') AS logicalScope, " +
        "coalesce(state.contractId, '') AS contractId, " +
        "coalesce(state.contractVersion, 0) AS contractVersion, " +
        "coalesce(state.routeEpoch, 0) AS routeEpoch, " +
        "coalesce(state.sourceActorId, '') AS sourceActorId, " +
        "coalesce(state.sourceVersion, 0) AS sourceVersion, " +
        "coalesce(state.sourceEventId, '') AS sourceEventId";

    internal static string BuildReadEventCypher(string eventLabel) =>
        $"MATCH (event:{eventLabel} {{physicalNamespace: $physicalNamespace, " +
        "projectionOwnerId: $ownerId, eventId: $eventId}) " +
        "RETURN coalesce(event.sourceActorId, '') AS sourceActorId, " +
        "coalesce(event.sourceVersion, 0) AS sourceVersion, " +
        "coalesce(event.deltaFingerprint, '') AS deltaFingerprint";

    /// <summary>
    /// Owned element identities of one owner in one physical namespace, read inside the apply
    /// transaction for repair/cutover replacements. Two plain label matches joined by UNION ALL:
    /// the same predicates the owner snapshot read uses, without payloads or subqueries.
    /// </summary>
    internal static string BuildReadOwnedElementIdsCypher(string nodeLabel, string edgeIdentityLabel) =>
        $"MATCH (node:{nodeLabel} {{scope: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "WHERE node.projectionGraphVersion = 2 " +
        "RETURN 'node' AS kind, node.nodeId AS id " +
        "UNION ALL " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "WHERE identity.projectionGraphVersion = 2 " +
        "RETURN CASE WHEN identity.status = 'pending' THEN 'pending' ELSE 'edge' END AS kind, " +
        "identity.edgeId AS id";

    internal static string BuildReadTouchedNodesCypher(string nodeLabel) =>
        $"MATCH (node:{nodeLabel} {{scope: $physicalNamespace}}) " +
        "WHERE node.nodeId IN $nodeIds " +
        "RETURN node.nodeId AS nodeId, " +
        "coalesce(node.projectionOwnerId, '') AS projectionOwnerId, " +
        "coalesce(node.projectionGraphVersion, 0) AS projectionGraphVersion";

    internal static string BuildReadTouchedEdgeIdentitiesCypher(string edgeIdentityLabel) =>
        $"MATCH (edge:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace}}) " +
        "WHERE edge.edgeId IN $edgeIds " +
        "RETURN edge.edgeId AS edgeId, " +
        "coalesce(edge.projectionOwnerId, '') AS projectionOwnerId, " +
        "coalesce(edge.projectionGraphVersion, 0) AS projectionGraphVersion";

    internal static string BuildReadTouchedRelationshipsCypher(string edgeType) =>
        $"MATCH ()-[edge:{edgeType} {{scope: $physicalNamespace}}]->() " +
        "WHERE edge.edgeId IN $edgeIds " +
        "RETURN edge.edgeId AS edgeId, " +
        "coalesce(edge.projectionOwnerId, '') AS projectionOwnerId, " +
        "coalesce(edge.projectionGraphVersion, 0) AS projectionGraphVersion";

    internal static string BuildReadForeignIncidentRelationshipsCypher(string nodeLabel, string edgeType) =>
        $"MATCH (node:{nodeLabel} {{scope: $physicalNamespace}})-[edge:{edgeType}]-() " +
        "WHERE node.nodeId IN $nodeIds " +
        "AND (coalesce(edge.projectionOwnerId, '') <> $ownerId " +
        "OR coalesce(edge.projectionGraphVersion, 0) <> 2) " +
        "RETURN DISTINCT edge.edgeId AS edgeId LIMIT 1";

    internal static string BuildReadForeignIncidentEdgeIdentitiesCypher(string edgeIdentityLabel) =>
        $"MATCH (edge:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace}}) " +
        "WHERE (edge.fromNodeId IN $nodeIds OR edge.toNodeId IN $nodeIds) " +
        "AND (coalesce(edge.projectionOwnerId, '') <> $ownerId " +
        "OR coalesce(edge.projectionGraphVersion, 0) <> 2) " +
        "RETURN edge.edgeId AS edgeId LIMIT 1";

    internal static string BuildDeleteNodesCypher(
        string nodeLabel,
        string edgeType,
        string edgeIdentityLabel) =>
        "UNWIND $nodeIds AS nodeId " +
        $"OPTIONAL MATCH (node:{nodeLabel} {{scope: $physicalNamespace, nodeId: nodeId}}) " +
        "WHERE node.projectionOwnerId = $ownerId " +
        $"OPTIONAL MATCH (node)-[edge:{edgeType} {{scope: $physicalNamespace}}]-() " +
        "WHERE edge.projectionOwnerId = $ownerId " +
        "WITH nodeId, node, collect(DISTINCT edge.edgeId) AS edgeIds " +
        "FOREACH (ignored IN CASE WHEN node IS NULL THEN [] ELSE [1] END | DETACH DELETE node) " +
        "WITH collect(nodeId) AS nodeIds, reduce(allEdgeIds = [], ids IN collect(edgeIds) | allEdgeIds + ids) AS edgeIds " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "WHERE identity.edgeId IN edgeIds OR identity.fromNodeId IN nodeIds OR identity.toNodeId IN nodeIds " +
        "DELETE identity";

    internal static string BuildDeleteEdgesCypher(string edgeType, string edgeIdentityLabel) =>
        "UNWIND $edgeIds AS edgeId " +
        $"OPTIONAL MATCH ()-[edge:{edgeType} {{scope: $physicalNamespace, edgeId: edgeId}}]->() " +
        "WHERE edge.projectionOwnerId = $ownerId " +
        "FOREACH (ignored IN CASE WHEN edge IS NULL THEN [] ELSE [1] END | DELETE edge) " +
        "WITH DISTINCT edgeId " +
        $"OPTIONAL MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, edgeId: edgeId}}) " +
        "WHERE identity.projectionOwnerId = $ownerId " +
        "FOREACH (ignored IN CASE WHEN identity IS NULL THEN [] ELSE [1] END | DELETE identity)";

    internal static string BuildUpsertNodesCypher(string nodeLabel) =>
        "UNWIND $nodes AS item " +
        $"MERGE (node:{nodeLabel} {{scope: $physicalNamespace, nodeId: item.nodeId}}) " +
        "ON CREATE SET node.projectionOwnerId = $ownerId, node.projectionManaged = true " +
        "WITH node, item WHERE node.projectionOwnerId = $ownerId " +
        "SET node.nodeType = item.nodeType, node.mutationPayload = item.mutationPayload, " +
        "node.updatedAtEpochMs = item.updatedAtEpochMs, node.projectionManaged = true, " +
        "node.projectionGraphVersion = item.projectionGraphVersion " +
        "RETURN count(node) AS appliedCount";

    internal static string BuildUpsertEdgeIdentitiesCypher(string edgeIdentityLabel) =>
        "UNWIND $edges AS item " +
        $"MERGE (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, edgeId: item.edgeId}}) " +
        "ON CREATE SET identity.projectionOwnerId = $ownerId " +
        "WITH identity, item WHERE identity.projectionOwnerId = $ownerId " +
        "SET identity.fromNodeId = item.fromNodeId, identity.toNodeId = item.toNodeId, " +
        "identity.relationType = item.relationType, identity.mutationPayload = item.mutationPayload, " +
        "identity.updatedAtEpochMs = item.updatedAtEpochMs, identity.status = item.status, " +
        "identity.projectionGraphVersion = item.projectionGraphVersion " +
        "RETURN count(identity) AS appliedCount";

    internal static string BuildDeleteRelationshipsForRewireCypher(string edgeType) =>
        $"MATCH ()-[edge:{edgeType} {{scope: $physicalNamespace}}]->() " +
        "WHERE edge.edgeId IN $edgeIds AND edge.projectionOwnerId = $ownerId DELETE edge";

    internal static string BuildCreateLiveEdgesCypher(string nodeLabel, string edgeType) =>
        "UNWIND $edges AS item " +
        $"MATCH (from:{nodeLabel} {{scope: $physicalNamespace, nodeId: item.fromNodeId}}) " +
        $"MATCH (to:{nodeLabel} {{scope: $physicalNamespace, nodeId: item.toNodeId}}) " +
        "WHERE from.projectionOwnerId = $ownerId AND to.projectionOwnerId = $ownerId " +
        $"CREATE (from)-[edge:{edgeType} {{scope: $physicalNamespace, edgeId: item.edgeId}}]->(to) " +
        "SET edge.relationType = item.relationType, edge.mutationPayload = item.mutationPayload, " +
        "edge.updatedAtEpochMs = item.updatedAtEpochMs, edge.projectionManaged = true, " +
        "edge.projectionOwnerId = $ownerId, edge.projectionGraphVersion = item.projectionGraphVersion " +
        "RETURN count(edge) AS appliedCount";

    // Promotion remains a separate read followed by a write. Each selection query is UNWIND-driven
    // and uses one exact indexed identity path; a single OR query over large node-id batches can
    // exhaust the Neo4j planner/runtime heap before the relationship MERGE even starts.
    internal static string BuildSelectPromotablePendingEdgesByIdCypher(string edgeIdentityLabel) =>
        "UNWIND $promotableEdgeIds AS edgeId " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, edgeId: edgeId}}) " +
        "WHERE identity.projectionOwnerId = $ownerId AND identity.status = 'pending' " +
        "RETURN identity.edgeId AS edgeId, identity.fromNodeId AS fromNodeId, identity.toNodeId AS toNodeId";

    internal static string BuildSelectPromotablePendingEdgesByFromNodeCypher(string edgeIdentityLabel) =>
        "UNWIND $promotableNodeIds AS nodeId " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, " +
        "projectionOwnerId: $ownerId, status: 'pending', fromNodeId: nodeId}) " +
        "RETURN identity.edgeId AS edgeId, identity.fromNodeId AS fromNodeId, identity.toNodeId AS toNodeId";

    internal static string BuildSelectPromotablePendingEdgesByToNodeCypher(string edgeIdentityLabel) =>
        "UNWIND $promotableNodeIds AS nodeId " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, " +
        "projectionOwnerId: $ownerId, status: 'pending', toNodeId: nodeId}) " +
        "RETURN identity.edgeId AS edgeId, identity.fromNodeId AS fromNodeId, identity.toNodeId AS toNodeId";

    internal static string BuildPromotePendingEdgesCypher(
        string nodeLabel,
        string edgeType,
        string edgeIdentityLabel) =>
        "UNWIND $promotable AS item " +
        $"MATCH (identity:{edgeIdentityLabel} {{physicalNamespace: $physicalNamespace, edgeId: item.edgeId}}) " +
        "WHERE identity.projectionOwnerId = $ownerId AND identity.status = 'pending' " +
        $"MATCH (from:{nodeLabel} {{scope: $physicalNamespace, nodeId: item.fromNodeId}}) " +
        $"MATCH (to:{nodeLabel} {{scope: $physicalNamespace, nodeId: item.toNodeId}}) " +
        "WHERE from.projectionOwnerId = $ownerId AND to.projectionOwnerId = $ownerId " +
        $"MERGE (from)-[edge:{edgeType} {{scope: $physicalNamespace, edgeId: identity.edgeId}}]->(to) " +
        "SET edge.relationType = identity.relationType, edge.mutationPayload = identity.mutationPayload, " +
        "edge.updatedAtEpochMs = identity.updatedAtEpochMs, edge.projectionManaged = true, " +
        "edge.projectionOwnerId = $ownerId, edge.projectionGraphVersion = identity.projectionGraphVersion, " +
        "identity.status = 'live' " +
        "RETURN count(edge) AS appliedCount";

    internal static string BuildCommitWatermarkCypher(string ownerStateLabel, string eventLabel) =>
        $"MATCH (state:{ownerStateLabel} {{physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "SET state.initialized = true, state.projectionKind = $projectionKind, " +
        "state.logicalScope = $logicalScope, state.contractId = $contractId, " +
        "state.contractVersion = $contractVersion, state.routeEpoch = $routeEpoch, " +
        "state.sourceActorId = $sourceActorId, state.sourceVersion = $sourceVersion, " +
        "state.sourceEventId = $eventId, state.deltaFingerprint = $deltaFingerprint " +
        $"CREATE (event:{eventLabel} {{physicalNamespace: $physicalNamespace, " +
        "projectionOwnerId: $ownerId, eventId: $eventId}) " +
        "SET event.sourceActorId = $sourceActorId, event.sourceVersion = $sourceVersion, " +
        "event.deltaFingerprint = $deltaFingerprint";

    internal static string BuildReadOwnerSnapshotCypher(
        string ownerStateLabel,
        string nodeLabel,
        string edgeType,
        string edgeIdentityLabel) =>
        $"MATCH (state:{ownerStateLabel} {{physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId}}) " +
        "WHERE coalesce(state.initialized, false) = true " +
        "CALL { WITH state " +
        $"OPTIONAL MATCH (node:{nodeLabel}) " +
        "WHERE node.scope = state.physicalNamespace AND node.projectionOwnerId = state.projectionOwnerId " +
        "AND node.projectionGraphVersion = 2 " +
        "RETURN collect(CASE WHEN node IS NULL THEN null ELSE {nodeId: node.nodeId, " +
        "mutationPayload: node.mutationPayload} END) AS nodes } " +
        "CALL { WITH state " +
        $"OPTIONAL MATCH ()-[edge:{edgeType}]->() " +
        "WHERE edge.scope = state.physicalNamespace AND edge.projectionOwnerId = state.projectionOwnerId " +
        "AND edge.projectionGraphVersion = 2 " +
        "RETURN collect(CASE WHEN edge IS NULL THEN null ELSE {edgeId: edge.edgeId, " +
        "mutationPayload: edge.mutationPayload} END) AS edges } " +
        "CALL { WITH state " +
        $"OPTIONAL MATCH (pending:{edgeIdentityLabel}) " +
        "WHERE pending.physicalNamespace = state.physicalNamespace " +
        "AND pending.projectionOwnerId = state.projectionOwnerId AND pending.status = 'pending' " +
        "AND pending.projectionGraphVersion = 2 " +
        "RETURN collect(CASE WHEN pending IS NULL THEN null ELSE {edgeId: pending.edgeId, " +
        "mutationPayload: pending.mutationPayload} END) AS pendingEdges } " +
        "RETURN state.projectionKind AS projectionKind, state.logicalScope AS logicalScope, " +
        "state.contractId AS contractId, state.contractVersion AS contractVersion, " +
        "state.routeEpoch AS routeEpoch, state.sourceActorId AS sourceActorId, " +
        "state.sourceVersion AS sourceVersion, state.sourceEventId AS sourceEventId, " +
        "nodes, edges, pendingEdges";
}
