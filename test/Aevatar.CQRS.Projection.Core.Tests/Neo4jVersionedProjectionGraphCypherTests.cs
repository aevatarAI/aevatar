using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class Neo4jVersionedProjectionGraphCypherTests
{
    [Fact]
    public void SchemaCypher_ShouldKeyOwnerEventsAndEdgesByPhysicalNamespace()
    {
        var owner = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateOwnerStateConstraintCypher("OwnerState", "owner_constraint");
        var events = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateEventConstraintCypher("OwnerEvent", "event_constraint");
        var edges = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateEdgeIdentityConstraintCypher("EdgeIdentity", "edge_constraint");
        var pendingFrom = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreatePendingFromIndexCypher("EdgeIdentity", "pending_from");
        var pendingTo = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreatePendingToIndexCypher("EdgeIdentity", "pending_to");

        owner.Should().Contain("(n.physicalNamespace, n.projectionOwnerId) IS UNIQUE");
        events.Should().Contain("(n.physicalNamespace, n.projectionOwnerId, n.eventId) IS UNIQUE");
        edges.Should().Contain("(n.physicalNamespace, n.edgeId) IS UNIQUE");
        pendingFrom.Should().Contain("n.status, n.fromNodeId");
        pendingTo.Should().Contain("n.status, n.toNodeId");
        var relationshipEdgeId = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateRelationshipEdgeIdIndexCypher("PROJECTION_REL", "rel_edge_id");
        relationshipEdgeId.Should().Contain("CREATE RANGE INDEX rel_edge_id IF NOT EXISTS");
        relationshipEdgeId.Should().Contain("FOR ()-[r:PROJECTION_REL]-() ON (r.scope, r.edgeId)");
    }

    [Fact]
    public void EdgeIdentityOwnerIndexCypher_ShouldBeARangeIndexOnNamespaceAndOwner()
    {
        var cypher = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateEdgeIdentityOwnerIndexCypher("EdgeIdentity", "edge_identity_owner");

        // Exact-match seek key of the owned-element-ids read and the owner snapshot read; the
        // pending (…, status, fromNodeId/toNodeId) indexes only allow a prefix scan for it.
        cypher.Should().StartWith("CREATE RANGE INDEX edge_identity_owner IF NOT EXISTS ");
        cypher.Should().EndWith("FOR (n:EdgeIdentity) ON (n.physicalNamespace, n.projectionOwnerId)");
        var ownedElementIds = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildReadOwnedElementIdsCypher("Node", "EdgeIdentity");
        ownedElementIds.Should().Contain(
            "(identity:EdgeIdentity {physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId})",
            "the index must cover exactly the identity match predicates");
    }

    [Fact]
    public void EveryVersionedCypherStatement_ShouldHaveBalancedBracesAndNoEscapedBraceResidue()
    {
        // Mixed interpolated/plain string segments once left "}}" in the emitted Cypher and
        // Neo4j rejected the statement at runtime; the InMemory conformance path never sees it.
        var statements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner-constraint"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateOwnerStateConstraintCypher("OwnerState", "c1"),
            ["event-constraint"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEventConstraintCypher("OwnerEvent", "c2"),
            ["edge-constraint"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEdgeIdentityConstraintCypher("EdgeIdentity", "c3"),
            ["pending-from"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreatePendingFromIndexCypher("EdgeIdentity", "i1"),
            ["pending-to"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreatePendingToIndexCypher("EdgeIdentity", "i2"),
            ["rel-edge-id"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateRelationshipEdgeIdIndexCypher("REL", "i3"),
            ["edge-identity-owner"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEdgeIdentityOwnerIndexCypher("EdgeIdentity", "i4"),
            ["lock-owner"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildLockOwnerStateCypher("OwnerState"),
            ["read-event"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadEventCypher("OwnerEvent"),
            ["owned-element-ids"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnedElementIdsCypher("Node", "EdgeIdentity"),
            ["touched-nodes"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedNodesCypher("Node"),
            ["touched-edge-identities"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedEdgeIdentitiesCypher("EdgeIdentity"),
            ["touched-relationships"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadTouchedRelationshipsCypher("REL"),
            ["foreign-relationships"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadForeignIncidentRelationshipsCypher("Node", "REL"),
            ["foreign-edge-identities"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadForeignIncidentEdgeIdentitiesCypher("EdgeIdentity"),
            ["delete-nodes"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteNodesCypher("Node", "REL", "EdgeIdentity"),
            ["delete-edges"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteEdgesCypher("REL", "EdgeIdentity"),
            ["upsert-nodes"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildUpsertNodesCypher("Node"),
            ["upsert-edge-identities"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildUpsertEdgeIdentitiesCypher("EdgeIdentity"),
            ["delete-rewire"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildDeleteRelationshipsForRewireCypher("REL"),
            ["create-live-edges"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateLiveEdgesCypher("Node", "REL"),
            ["select-promotable-by-id"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByIdCypher("EdgeIdentity"),
            ["select-promotable-by-from"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByFromNodeCypher("EdgeIdentity"),
            ["select-promotable-by-to"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildSelectPromotablePendingEdgesByToNodeCypher("EdgeIdentity"),
            ["promote-pending"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildPromotePendingEdgesCypher("Node", "REL", "EdgeIdentity"),
            ["commit-watermark"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCommitWatermarkCypher("OwnerState", "OwnerEvent"),
            ["read-snapshot"] = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnerSnapshotCypher("OwnerState", "Node", "REL", "EdgeIdentity"),
        };

        foreach (var (name, cypher) in statements)
        {
            cypher.Should().NotContain("}}", because: $"{name} must not carry escaped-brace residue");
            cypher.Should().NotContain("{{", because: $"{name} must not carry escaped-brace residue");
            cypher.Count(static c => c == '{').Should().Be(
                cypher.Count(static c => c == '}'),
                because: $"{name} must have balanced braces");
            cypher.Count(static c => c == '(').Should().Be(
                cypher.Count(static c => c == ')'),
                because: $"{name} must have balanced parentheses");
        }
    }

    [Fact]
    public void OwnedElementIdsCypher_ShouldReadOnlyThisOwnersV2ElementsByKind()
    {
        var cypher = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildReadOwnedElementIdsCypher("Node", "EdgeIdentity");

        // Two plain label matches keyed by (namespace, owner) joined by UNION ALL: no payloads, no
        // subqueries, and every kind the replacement can delete (node, live edge, pending edge).
        cypher.Should().Contain("MATCH (node:Node {scope: $physicalNamespace, projectionOwnerId: $ownerId})");
        cypher.Should().Contain("MATCH (identity:EdgeIdentity {physicalNamespace: $physicalNamespace, projectionOwnerId: $ownerId})");
        cypher.Should().Contain("UNION ALL");
        cypher.Should().Contain("RETURN 'node' AS kind, node.nodeId AS id");
        cypher.Should().Contain("CASE WHEN identity.status = 'pending' THEN 'pending' ELSE 'edge' END AS kind");
        cypher.Should().Contain("projectionGraphVersion = 2");
        cypher.Should().NotContain("CALL {");
        cypher.Should().NotContain("mutationPayload");
    }

    [Fact]
    public void ApplyCypher_ShouldPersistFullRouteAndKeepWatermarkLast()
    {
        var lockOwner = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildLockOwnerStateCypher("OwnerState");
        var commit = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCommitWatermarkCypher("OwnerState", "OwnerEvent");
        var edgeIdentities = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildUpsertEdgeIdentitiesCypher("EdgeIdentity");
        var promote = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildPromotePendingEdgesCypher("GraphNode", "GRAPH_REL", "EdgeIdentity");

        lockOwner.Should().Contain("SET state.lockToken");
        lockOwner.Should().Contain("coalesce(state.contractId, '')");
        commit.Should().Contain("state.contractId = $contractId");
        commit.Should().Contain("state.contractVersion = $contractVersion");
        commit.Should().Contain("state.routeEpoch = $routeEpoch");
        commit.Should().Contain("CREATE (event:OwnerEvent");
        edgeIdentities.Should().Contain("identity.status = item.status");
        edgeIdentities.Should().Contain("identity.mutationPayload = item.mutationPayload");
        edgeIdentities.Should().Contain("identity.projectionGraphVersion = item.projectionGraphVersion");
        var selectById = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildSelectPromotablePendingEdgesByIdCypher("EdgeIdentity");
        var selectByFrom = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildSelectPromotablePendingEdgesByFromNodeCypher("EdgeIdentity");
        var selectByTo = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildSelectPromotablePendingEdgesByToNodeCypher("EdgeIdentity");
        selectById.Should().StartWith("UNWIND $promotableEdgeIds AS edgeId ");
        selectById.Should().Contain("edgeId: edgeId");
        selectByFrom.Should().StartWith("UNWIND $promotableNodeIds AS nodeId ");
        selectByFrom.Should().Contain("status: 'pending', fromNodeId: nodeId");
        selectByTo.Should().StartWith("UNWIND $promotableNodeIds AS nodeId ");
        selectByTo.Should().Contain("status: 'pending', toNodeId: nodeId");
        new[] { selectById, selectByFrom, selectByTo }.Should().OnlyContain(
            statement => !statement.Contains(" OR ", StringComparison.Ordinal) &&
                         !statement.Contains("MERGE", StringComparison.Ordinal));
        // Selection and MERGE stay in separate statements, and every statement is UNWIND-driven
        // with no disjunction for the Neo4j planner to expand across a large id batch.
        promote.Should().StartWith("UNWIND $promotable AS item ");
        promote.Should().NotContain(" OR ");
        promote.Should().Contain("identity.status = 'pending'");
        promote.Should().Contain("MERGE (from)-[edge:");
        promote.Should().Contain("identity.status = 'live'");
        promote.Should().NotContain("Unknown");
    }

    [Fact]
    public void SnapshotCypher_ShouldReadStateNodesEdgesAndPendingEdgesInOneStatement()
    {
        var snapshot = Neo4jProjectionGraphStoreVersionedCypherSupport.BuildReadOwnerSnapshotCypher(
            "OwnerState",
            "GraphNode",
            "GRAPH_REL",
            "EdgeIdentity");

        CountOccurrences(snapshot, "CALL { WITH state").Should().Be(3);
        snapshot.Should().Contain("state.contractId AS contractId");
        snapshot.Should().Contain("state.contractVersion AS contractVersion");
        snapshot.Should().Contain("state.routeEpoch AS routeEpoch");
        snapshot.Should().Contain("nodes, edges, pendingEdges");
        snapshot.Should().Contain("projectionGraphVersion = 2");
    }

    [Fact]
    public void RewireCypher_ShouldDeleteOldRelationshipBeforeCreatingTheNewEndpointPair()
    {
        var delete = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildDeleteRelationshipsForRewireCypher("GRAPH_REL");
        var create = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildCreateLiveEdgesCypher("GraphNode", "GRAPH_REL");

        delete.Should().Contain("edge.edgeId IN $edgeIds");
        delete.Should().Contain("DELETE edge");
        create.Should().Contain("nodeId: item.fromNodeId");
        create.Should().Contain("nodeId: item.toNodeId");
        create.Should().Contain("projectionGraphVersion = item.projectionGraphVersion");
        create.Should().Contain("mutationPayload = item.mutationPayload");
        create.Should().NotContain("Unknown");
    }

    [Fact]
    public void NodeDeletionCypher_ShouldRejectIncidentEdgesWithoutV2OwnerProvenance()
    {
        var relationships = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildReadForeignIncidentRelationshipsCypher("GraphNode", "GRAPH_REL");
        var identities = Neo4jProjectionGraphStoreVersionedCypherSupport
            .BuildReadForeignIncidentEdgeIdentitiesCypher("EdgeIdentity");

        relationships.Should().Contain("coalesce(edge.projectionOwnerId, '') <> $ownerId");
        relationships.Should().Contain("coalesce(edge.projectionGraphVersion, 0) <> 2");
        identities.Should().Contain("coalesce(edge.projectionOwnerId, '') <> $ownerId");
        identities.Should().Contain("coalesce(edge.projectionGraphVersion, 0) <> 2");
    }

    private static int CountOccurrences(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
