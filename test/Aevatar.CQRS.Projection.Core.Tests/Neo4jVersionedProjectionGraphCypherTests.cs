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
        promote.Should().Contain("status: 'pending'");
        promote.Should().Contain("identity.edgeId IN $promotableEdgeIds");
        promote.Should().Contain("identity.fromNodeId IN $promotableNodeIds");
        promote.Should().Contain("identity.toNodeId IN $promotableNodeIds");
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
