using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class VersionedProjectionGraphStoreConformanceTests
{
    [Fact]
    public async Task InMemory_ShouldConformToVersionedOwnerGraphContract()
    {
        var store = new InMemoryProjectionGraphStore();

        await AssertConformanceAsync(store, $"memory-{Guid.NewGuid():N}");
    }

    [Neo4jIntegrationFact]
    [Trait("Category", "ProviderIntegration")]
    [Trait("Feature", "ProjectionProviders")]
    public async Task Neo4j_ShouldConformToVersionedOwnerGraphContract()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphDelta{suffix}";
        var edgeType = $"PROJECTION_DELTA_{suffix}";
        var options = CreateNeo4jOptions(nodeLabel, edgeType);
        await using var driver = GraphDatabase.Driver(
            options.Uri,
            AuthTokens.Basic(options.Username, options.Password));
        var store = new Neo4jProjectionGraphStore(options);

        try
        {
            await AssertConformanceAsync(store, $"neo4j-{suffix}");
        }
        finally
        {
            await store.DisposeAsync();
            await CleanupNeo4jAsync(driver, options.Database, nodeLabel, edgeType);
        }
    }

    [Fact]
    public async Task ApplyDelta_WhenMutationCategoryChanges_ShouldNotTreatPayloadAsExactDuplicate()
    {
        var store = new InMemoryProjectionGraphStore();
        var route = Route($"fingerprint-{Guid.NewGuid():N}");
        var first = Delta(route, 1, "event-1");
        first.DeleteNodeIds.Add("same-token");
        var conflicting = Delta(route, 1, "event-1");
        conflicting.DeleteEdgeIds.Add("same-token");

        var applied = await store.ApplyDeltaAsync(first);
        var conflict = await store.ApplyDeltaAsync(conflicting);

        applied.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        conflict.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.EventIdConflict);
    }

    [Fact]
    public async Task ApplyDelta_WhenTimestampExceedsDateTimeRange_ShouldNormalizeBeforeMutating()
    {
        var store = new InMemoryProjectionGraphStore();
        var route = Route($"timestamp-{Guid.NewGuid():N}");
        var delta = Delta(route, 1, "event-1");
        delta.UpsertNodes.Add(new ProjectionGraphNodeMutation
        {
            NodeId = "root",
            NodeType = "Run",
            UpdatedAtEpochMs = long.MaxValue,
        });

        var result = await store.ApplyDeltaAsync(delta);
        var snapshot = await store.ReadOwnerSnapshotAsync(route);

        result.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        snapshot.Disposition.Should().Be(ProjectionGraphOwnerSnapshotReadDisposition.Found);
        snapshot.Snapshot.Source.StateVersion.Should().Be(1);
        snapshot.Snapshot.Nodes.Should().ContainSingle()
            .Which.UpdatedAtEpochMs.Should().Be(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ApplyDelta_WhenLegacyEdgeReferencesDeletedNode_ShouldRejectWithoutAdvancingWatermark()
    {
        var store = new InMemoryProjectionGraphStore();
        var route = Route($"legacy-incident-{Guid.NewGuid():N}");
        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("root"));
        initial.UpsertNodes.Add(Node("target"));
        (await store.ApplyDeltaAsync(initial)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        await store.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = route.PhysicalNamespace,
            EdgeId = "legacy-edge",
            FromNodeId = "root",
            ToNodeId = "target",
            EdgeType = "LEGACY",
        });
        var deletion = Delta(route, 2, "event-2");
        deletion.DeleteNodeIds.Add("target");

        var result = await store.ApplyDeltaAsync(deletion);
        var snapshot = await store.ReadOwnerSnapshotAsync(route);

        result.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision);
        snapshot.Snapshot.Source.StateVersion.Should().Be(1);
        snapshot.Snapshot.Nodes.Select(x => x.NodeId).Should().BeEquivalentTo("root", "target");
    }

    [Fact]
    public void OwnerIdentityResolver_ShouldExposeTheLegacyWriterIdentityRule()
    {
        var resolver = new ProjectionGraphOwnerIdentityResolver();

        var identity = resolver.Resolve(typeof(VersionedProjectionGraphStoreConformanceTests), " owner-7 ");

        identity.Value.Should().Be(
            $"{typeof(VersionedProjectionGraphStoreConformanceTests).FullName}:owner-7");
    }

    private static async Task AssertConformanceAsync(
        IVersionedProjectionGraphStore store,
        string namespacePrefix)
    {
        await AssertVersionAndFailureWatermarksAsync(store, $"{namespacePrefix}-versions");
        await AssertRoutesAndOwnerCollisionsAsync(store, $"{namespacePrefix}-routes");
        await AssertPendingPromotionAndRewireAsync(store, $"{namespacePrefix}-pending");
        await AssertRepairJumpAsync(store, $"{namespacePrefix}-repair");
        await AssertSameVersionMaintenanceReplacementAsync(store, $"{namespacePrefix}-maintenance");
        await AssertRepairReplacementAsync(store, $"{namespacePrefix}-replace");
        await AssertRepairReplacementKeepsNodeAndEdgeIdentitySpacesApartAsync(
            store,
            $"{namespacePrefix}-identity-spaces");
        await AssertRepairReplacementMutationBoundAsync(store, $"{namespacePrefix}-bound");
    }

    private static async Task AssertSameVersionMaintenanceReplacementAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var ordinary = Delta(route, 1, "event-1");
        ordinary.UpsertNodes.Add(Node("stale-running"));
        (await store.ApplyDeltaAsync(ordinary)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var maintenance = RepairDelta(
            route,
            1,
            CommittedStateRepublish.BuildEventId("actor-1", 1));
        maintenance.UpsertNodes.Add(Node("authoritative-terminal"));
        (await store.ApplyDeltaAsync(maintenance)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var repaired = await store.ReadOwnerSnapshotAsync(route);
        repaired.Snapshot.Source.Should().BeEquivalentTo(maintenance.Source);
        repaired.Snapshot.Nodes.Select(static node => node.NodeId)
            .Should().Equal("authoritative-terminal");
        (await store.ApplyDeltaAsync(maintenance.Clone())).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.ExactDuplicate);

        var delayedOrdinary = Delta(route, 1, "event-delayed");
        delayedOrdinary.UpsertNodes.Add(Node("stale-running"));
        (await store.ApplyDeltaAsync(delayedOrdinary)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Stale);
        (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Should()
            .BeEquivalentTo(repaired.Snapshot);
    }

    /// <summary>
    /// Node ids and edge ids (live + pending) are separate identity spaces: a repair/cutover
    /// replacement reconciles each category only against its own desired and explicit-delete
    /// sets, so a node and an edge that share one id token are kept or deleted independently.
    /// </summary>
    private static async Task AssertRepairReplacementKeepsNodeAndEdgeIdentitySpacesApartAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("a"));
        initial.UpsertNodes.Add(Node("b"));
        initial.UpsertNodes.Add(Node("shared"));
        initial.UpsertEdges.Add(Edge("shared", "a", "b"));
        initial.UpsertPendingEdges.Add(Edge("shared-pending", "a", "late"));
        (await store.ApplyDeltaAsync(initial)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var seeded = await store.ReadOwnerSnapshotAsync(route);
        seeded.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("a", "b", "shared");
        seeded.Snapshot.Edges.Select(x => x.EdgeId).Should().Equal("shared");
        seeded.Snapshot.PendingEdges.Select(x => x.EdgeId).Should().Equal("shared-pending");

        // Keep the node "shared"; the edge "shared" and the pending edge are stale and go.
        var keepNode = RepairDelta(route, 2, "event-keep-node");
        keepNode.UpsertNodes.Add(Node("a"));
        keepNode.UpsertNodes.Add(Node("b"));
        keepNode.UpsertNodes.Add(Node("shared"));
        (await store.ApplyDeltaAsync(keepNode)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var nodeKept = await store.ReadOwnerSnapshotAsync(route);
        nodeKept.Snapshot.Source.StateVersion.Should().Be(2);
        nodeKept.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("a", "b", "shared");
        nodeKept.Snapshot.Edges.Should().BeEmpty("the edge sharing the kept node's token is stale");
        nodeKept.Snapshot.PendingEdges.Should().BeEmpty();

        // Restore both edges, then keep them and drop the node that shares the live edge's token.
        var restoreEdges = Delta(route, 3, "event-3");
        restoreEdges.UpsertEdges.Add(Edge("shared", "a", "b"));
        restoreEdges.UpsertPendingEdges.Add(Edge("shared-pending", "a", "late"));
        (await store.ApplyDeltaAsync(restoreEdges)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var keepEdge = RepairDelta(route, 4, "event-keep-edge");
        keepEdge.UpsertNodes.Add(Node("a"));
        keepEdge.UpsertNodes.Add(Node("b"));
        keepEdge.UpsertEdges.Add(Edge("shared", "a", "b"));
        keepEdge.UpsertPendingEdges.Add(Edge("shared-pending", "a", "late"));
        (await store.ApplyDeltaAsync(keepEdge)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var edgeKept = await store.ReadOwnerSnapshotAsync(route);
        edgeKept.Snapshot.Source.StateVersion.Should().Be(4);
        edgeKept.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("a", "b");
        edgeKept.Snapshot.Edges.Select(x => x.EdgeId).Should().Equal("shared");
        edgeKept.Snapshot.PendingEdges.Select(x => x.EdgeId).Should().Equal("shared-pending");

        // Explicit deletes are category-scoped too: DeleteNodeIds=["shared"] neither targets nor
        // protects the stale edge "shared" ...
        var restoreNode = Delta(route, 5, "event-5");
        restoreNode.UpsertNodes.Add(Node("shared"));
        (await store.ApplyDeltaAsync(restoreNode)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var explicitNodeDelete = RepairDelta(route, 6, "event-explicit-node");
        explicitNodeDelete.UpsertNodes.Add(Node("a"));
        explicitNodeDelete.UpsertNodes.Add(Node("b"));
        explicitNodeDelete.DeleteNodeIds.Add("shared");
        (await store.ApplyDeltaAsync(explicitNodeDelete)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var afterExplicitNodeDelete = await store.ReadOwnerSnapshotAsync(route);
        afterExplicitNodeDelete.Snapshot.Source.StateVersion.Should().Be(6);
        afterExplicitNodeDelete.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("a", "b");
        afterExplicitNodeDelete.Snapshot.Edges.Should()
            .BeEmpty("an explicit node delete does not suppress the stale edge with the same token");
        afterExplicitNodeDelete.Snapshot.PendingEdges.Should().BeEmpty();

        // ... and DeleteEdgeIds=["shared"] neither targets nor protects the stale node "shared".
        var restoreAll = Delta(route, 7, "event-7");
        restoreAll.UpsertNodes.Add(Node("shared"));
        restoreAll.UpsertEdges.Add(Edge("shared", "a", "b"));
        restoreAll.UpsertPendingEdges.Add(Edge("shared-pending", "a", "late"));
        (await store.ApplyDeltaAsync(restoreAll)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var explicitEdgeDelete = RepairDelta(route, 8, "event-explicit-edge");
        explicitEdgeDelete.UpsertNodes.Add(Node("a"));
        explicitEdgeDelete.UpsertNodes.Add(Node("b"));
        explicitEdgeDelete.DeleteEdgeIds.Add("shared");
        (await store.ApplyDeltaAsync(explicitEdgeDelete)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var afterExplicitEdgeDelete = await store.ReadOwnerSnapshotAsync(route);
        afterExplicitEdgeDelete.Snapshot.Source.StateVersion.Should().Be(8);
        afterExplicitEdgeDelete.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal(
            new[] { "a", "b" },
            "an explicit edge delete does not suppress the stale node with the same token");
        afterExplicitEdgeDelete.Snapshot.Edges.Should().BeEmpty();
        afterExplicitEdgeDelete.Snapshot.PendingEdges.Should().BeEmpty();
    }

    /// <summary>
    /// A repair/cutover replacement is bounded by <see cref="ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount"/>
    /// over its effective mutations (upserts + explicit deletes + the stale deletes the store
    /// generates), counted inside the apply after the event-id and version checks and before any
    /// mutation. Overflow is rejected atomically: watermark, snapshot and event-id binding stay
    /// untouched, so a corrected delta with the same event id still applies.
    /// </summary>
    private static async Task AssertRepairReplacementMutationBoundAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        const int limit = ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount;
        const int half = limit / 2;
        var route = Route(physicalNamespace);
        var seed = Delta(route, 1, "event-seed");
        seed.UpsertNodes.AddRange(Nodes("old", half));
        (await store.ApplyDeltaAsync(seed)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var seeded = await store.ReadOwnerSnapshotAsync(route);
        seeded.Snapshot.Nodes.Should().HaveCount(half);

        // half upserts + half generated stale deletes + 1 = limit + 1: rejected before any mutation.
        var overflow = RepairDelta(route, 2, "event-cutover");
        overflow.UpsertNodes.AddRange(Nodes("new", half + 1));
        var overflowResult = await store.ApplyDeltaAsync(overflow);
        overflowResult.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.MutationBoundExceeded);
        overflowResult.CurrentSource.Should().BeEquivalentTo(seed.Source);
        var afterOverflow = await store.ReadOwnerSnapshotAsync(route);
        afterOverflow.Snapshot.Source.Should().BeEquivalentTo(seed.Source);
        NodeIds(afterOverflow).Should().Equal(NodeIds(seeded));
        afterOverflow.Snapshot.Edges.Should().BeEmpty();
        afterOverflow.Snapshot.PendingEdges.Should().BeEmpty();

        // The rejected event id was not bound: the corrected replacement with the same event id
        // sits exactly at the limit (half upserts + half stale deletes) and applies.
        var corrected = RepairDelta(route, 2, "event-cutover");
        corrected.UpsertNodes.AddRange(Nodes("new", half));
        (await store.ApplyDeltaAsync(corrected)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var replaced = await store.ReadOwnerSnapshotAsync(route);
        replaced.Snapshot.Source.Should().BeEquivalentTo(corrected.Source);
        NodeIds(replaced).Should().Equal(Nodes("new", half).Select(x => x.NodeId));
        replaced.Snapshot.Edges.Should().BeEmpty();

        // Event-id and version checks run before the bound: an over-limit delta that reuses the
        // bound event id conflicts, a stale one is stale, and the exact replay is a duplicate.
        var reusedEventId = RepairDelta(route, 3, "event-cutover");
        reusedEventId.UpsertNodes.AddRange(Nodes("later", half + 1));
        (await store.ApplyDeltaAsync(reusedEventId)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.EventIdConflict);
        var staleOverflow = RepairDelta(route, 1, "event-stale-cutover");
        staleOverflow.UpsertNodes.AddRange(Nodes("later", half + 1));
        (await store.ApplyDeltaAsync(staleOverflow)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Stale);
        (await store.ApplyDeltaAsync(corrected.Clone())).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.ExactDuplicate);
        var afterRejections = await store.ReadOwnerSnapshotAsync(route);
        afterRejections.Snapshot.Source.Should().BeEquivalentTo(corrected.Source);
        NodeIds(afterRejections).Should().Equal(NodeIds(replaced));

        // Explicit deletes count too. An explicit delete of an owned element replaces its stale
        // delete (no double count); an explicit delete of an element the owner never had adds one:
        // half upserts + 1 explicit node + 1 explicit edge + (half - 1) stale = limit + 1.
        var explicitOverflow = RepairDelta(route, 3, "event-explicit");
        explicitOverflow.UpsertNodes.AddRange(Nodes("later", half));
        explicitOverflow.DeleteNodeIds.Add(Nodes("new", 1).Single().NodeId);
        explicitOverflow.DeleteEdgeIds.Add("edge-never-owned");
        var explicitOverflowResult = await store.ApplyDeltaAsync(explicitOverflow);
        explicitOverflowResult.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.MutationBoundExceeded);
        explicitOverflowResult.CurrentSource.Should().BeEquivalentTo(corrected.Source);
        var afterExplicitOverflow = await store.ReadOwnerSnapshotAsync(route);
        afterExplicitOverflow.Snapshot.Source.Should().BeEquivalentTo(corrected.Source);
        NodeIds(afterExplicitOverflow).Should().Equal(NodeIds(replaced));

        // (half - 1) upserts + 1 explicit node + 1 explicit edge + (half - 1) stale = limit: applied.
        var explicitAtLimit = RepairDelta(route, 3, "event-explicit");
        explicitAtLimit.UpsertNodes.AddRange(Nodes("later", half - 1));
        explicitAtLimit.DeleteNodeIds.Add(Nodes("new", 1).Single().NodeId);
        explicitAtLimit.DeleteEdgeIds.Add("edge-never-owned");
        (await store.ApplyDeltaAsync(explicitAtLimit)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var afterExplicitAtLimit = await store.ReadOwnerSnapshotAsync(route);
        afterExplicitAtLimit.Snapshot.Source.Should().BeEquivalentTo(explicitAtLimit.Source);
        NodeIds(afterExplicitAtLimit).Should().Equal(Nodes("later", half - 1).Select(x => x.NodeId));
        afterExplicitAtLimit.Snapshot.Edges.Should().BeEmpty();
        afterExplicitAtLimit.Snapshot.PendingEdges.Should().BeEmpty();
    }

    /// <summary>
    /// A repair/cutover delta is the complete desired owner graph: the store deletes every owned
    /// element absent from it, so the producer never derives deletes from a pre-read snapshot
    /// and a replay after a committed-but-unacknowledged apply is an exact duplicate.
    /// </summary>
    private static async Task AssertRepairReplacementAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("root"));
        initial.UpsertNodes.Add(Node("old-step"));
        initial.UpsertNodes.Add(Node("dangling-target-owner"));
        initial.UpsertEdges.Add(Edge("root->old-step", "root", "old-step"));
        initial.UpsertPendingEdges.Add(Edge("old-step->late", "old-step", "late"));
        (await store.ApplyDeltaAsync(initial)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        // Another owner's element in the same namespace must survive the replacement untouched.
        var otherOwnerRoute = route.Clone();
        otherOwnerRoute.OwnerId = "owner-other";
        var other = Delta(otherOwnerRoute, 1, "event-other-1");
        other.UpsertNodes.Add(Node("other-root"));
        (await store.ApplyDeltaAsync(other)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var replacement = Delta(route, 5, "event-replace");
        replacement.Mode = ProjectionGraphDeltaMode.RepairOrCutover;
        replacement.UpsertNodes.Add(Node("root"));
        replacement.UpsertNodes.Add(Node("new-step"));
        replacement.UpsertEdges.Add(Edge("root->new-step", "root", "new-step"));
        (await store.ApplyDeltaAsync(replacement)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var replaced = await store.ReadOwnerSnapshotAsync(route);
        replaced.Snapshot.Source.StateVersion.Should().Be(5);
        replaced.Snapshot.Nodes.Select(x => x.NodeId).Should().BeEquivalentTo("root", "new-step");
        replaced.Snapshot.Edges.Select(x => x.EdgeId).Should().Equal("root->new-step");
        replaced.Snapshot.PendingEdges.Should().BeEmpty("stale pending edges are replaced too");
        (await store.ReadOwnerSnapshotAsync(otherOwnerRoute)).Snapshot.Nodes
            .Select(x => x.NodeId).Should().Equal("other-root");

        // Replay of the identical replacement against the already-replaced graph (commit succeeded,
        // acknowledgement lost): the fingerprint is a function of the desired graph only.
        (await store.ApplyDeltaAsync(replacement.Clone())).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.ExactDuplicate);
        (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Should().BeEquivalentTo(replaced.Snapshot);

        // Same event id, genuinely different desired graph: still a conflict.
        var divergent = Delta(route, 5, "event-replace");
        divergent.Mode = ProjectionGraphDeltaMode.RepairOrCutover;
        divergent.UpsertNodes.Add(Node("root"));
        divergent.UpsertNodes.Add(Node("other-step"));
        (await store.ApplyDeltaAsync(divergent)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.EventIdConflict);
        (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Should().BeEquivalentTo(replaced.Snapshot);

        // Version monotonicity survives the replacement: normal deltas continue from the watermark.
        (await store.ApplyDeltaAsync(Delta(route, 5, "event-same-version"))).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.SameVersionConflict);
        (await store.ApplyDeltaAsync(Delta(route, 4, "event-stale"))).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Stale);
        var next = Delta(route, 6, "event-6");
        next.UpsertNodes.Add(Node("late"));
        (await store.ApplyDeltaAsync(next)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Nodes
            .Select(x => x.NodeId).Should().BeEquivalentTo("root", "new-step", "late");
    }

    private static async Task AssertVersionAndFailureWatermarksAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var gap = Delta(route, 2, "event-gap");
        var gapResult = await store.ApplyDeltaAsync(gap);
        gapResult.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Gap);
        (await store.ReadOwnerSnapshotAsync(route)).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.NotFound);

        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("root", ("order-b", "2"), ("order-a", "1")));
        var applied = await store.ApplyDeltaAsync(initial);
        applied.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        applied.CurrentSource.Should().BeEquivalentTo(initial.Source);

        var exactWithDifferentMapInsertionOrder = Delta(route, 1, "event-1");
        exactWithDifferentMapInsertionOrder.UpsertNodes.Add(
            Node("root", ("order-a", "1"), ("order-b", "2")));
        var duplicate = await store.ApplyDeltaAsync(exactWithDifferentMapInsertionOrder);
        duplicate.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.ExactDuplicate);

        var sameVersion = await store.ApplyDeltaAsync(Delta(route, 1, "event-other"));
        sameVersion.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.SameVersionConflict);

        var reusedEventId = await store.ApplyDeltaAsync(Delta(route, 2, "event-1"));
        reusedEventId.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.EventIdConflict);

        var laterGap = await store.ApplyDeltaAsync(Delta(route, 3, "event-3"));
        laterGap.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Gap);

        var wrongActor = Delta(route, 2, "event-wrong-actor");
        wrongActor.Source.ActorId = "actor-other";
        (await store.ApplyDeltaAsync(wrongActor)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.SourceActorMismatch);

        var missingEndpoint = Delta(route, 2, "event-missing");
        missingEndpoint.UpsertEdges.Add(Edge("next", "root", "late"));
        (await store.ApplyDeltaAsync(missingEndpoint)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.MissingEndpoint);

        var afterFailures = await store.ReadOwnerSnapshotAsync(route);
        afterFailures.Disposition.Should().Be(ProjectionGraphOwnerSnapshotReadDisposition.Found);
        afterFailures.Snapshot.Source.StateVersion.Should().Be(1);
        afterFailures.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("root");
        afterFailures.Snapshot.Edges.Should().BeEmpty();

        var second = Delta(route, 2, "event-2");
        second.UpsertNodes.Add(Node("late"));
        second.UpsertEdges.Add(Edge("next", "root", "late"));
        (await store.ApplyDeltaAsync(second)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var stale = await store.ApplyDeltaAsync(Delta(route, 1, "event-stale"));
        stale.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Stale);
        var snapshot = await store.ReadOwnerSnapshotAsync(route);
        snapshot.Snapshot.Route.Should().BeEquivalentTo(route);
        snapshot.Snapshot.Source.Should().BeEquivalentTo(second.Source);
        snapshot.Snapshot.Nodes.Select(x => x.NodeId).Should().BeEquivalentTo("root", "late");
        snapshot.Snapshot.Edges.Select(x => x.EdgeId).Should().Equal("next");
    }

    private static async Task AssertRoutesAndOwnerCollisionsAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("shared-node"));
        (await store.ApplyDeltaAsync(initial)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var epochMismatchRoute = route.Clone();
        epochMismatchRoute.RouteEpoch++;
        (await store.ApplyDeltaAsync(Delta(epochMismatchRoute, 2, "event-2"))).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.RouteEpochMismatch);
        (await store.ReadOwnerSnapshotAsync(epochMismatchRoute)).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.RouteEpochMismatch);

        var contractMismatchRoute = route.Clone();
        contractMismatchRoute.ContractVersion++;
        (await store.ApplyDeltaAsync(Delta(contractMismatchRoute, 2, "event-contract"))).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.RouteFingerprintMismatch);
        (await store.ReadOwnerSnapshotAsync(contractMismatchRoute)).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.RouteFingerprintMismatch);

        var otherOwnerRoute = route.Clone();
        otherOwnerRoute.OwnerId = "owner-other";
        var collision = Delta(otherOwnerRoute, 1, "event-owner-other");
        collision.UpsertNodes.Add(Node("shared-node"));
        (await store.ApplyDeltaAsync(collision)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.CrossOwnerCollision);
        (await store.ReadOwnerSnapshotAsync(otherOwnerRoute)).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.NotFound);
    }

    private static async Task AssertPendingPromotionAndRewireAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var initial = Delta(route, 1, "event-1");
        initial.UpsertNodes.Add(Node("root"));
        initial.UpsertPendingEdges.Add(Edge("next", "root", "late"));
        (await store.ApplyDeltaAsync(initial)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);

        var pendingSnapshot = await store.ReadOwnerSnapshotAsync(route);
        pendingSnapshot.Snapshot.PendingEdges.Select(x => x.EdgeId).Should().Equal("next");
        pendingSnapshot.Snapshot.Edges.Should().BeEmpty();
        pendingSnapshot.Snapshot.Nodes.Select(x => x.NodeId).Should().Equal("root");

        var lateTarget = Delta(route, 2, "event-2");
        lateTarget.UpsertNodes.Add(Node("late"));
        (await store.ApplyDeltaAsync(lateTarget)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var promotedSnapshot = await store.ReadOwnerSnapshotAsync(route);
        promotedSnapshot.Snapshot.PendingEdges.Should().BeEmpty();
        promotedSnapshot.Snapshot.Edges.Should().ContainSingle()
            .Which.ToNodeId.Should().Be("late");

        var rewire = Delta(route, 3, "event-3");
        rewire.UpsertNodes.Add(Node("retry-target"));
        rewire.UpsertEdges.Add(Edge("next", "root", "retry-target"));
        (await store.ApplyDeltaAsync(rewire)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var rewiredSnapshot = await store.ReadOwnerSnapshotAsync(route);
        rewiredSnapshot.Snapshot.Edges.Should().ContainSingle();
        rewiredSnapshot.Snapshot.Edges[0].FromNodeId.Should().Be("root");
        rewiredSnapshot.Snapshot.Edges[0].ToNodeId.Should().Be("retry-target");

        var deleteTarget = Delta(route, 4, "event-4");
        deleteTarget.DeleteNodeIds.Add("retry-target");
        (await store.ApplyDeltaAsync(deleteTarget)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var deletedSnapshot = await store.ReadOwnerSnapshotAsync(route);
        deletedSnapshot.Snapshot.Source.StateVersion.Should().Be(4);
        deletedSnapshot.Snapshot.Nodes.Select(x => x.NodeId).Should().NotContain("retry-target");
        deletedSnapshot.Snapshot.Edges.Should().BeEmpty();
    }

    private static async Task AssertRepairJumpAsync(
        IVersionedProjectionGraphStore store,
        string physicalNamespace)
    {
        var route = Route(physicalNamespace);
        var repair = Delta(route, 10, "event-repair");
        repair.Mode = ProjectionGraphDeltaMode.RepairOrCutover;
        repair.UpsertNodes.Add(Node("recovered"));

        (await store.ApplyDeltaAsync(repair)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        (await store.ApplyDeltaAsync(Delta(route, 11, "event-11"))).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Source.StateVersion.Should().Be(11);
    }

    private static ProjectionGraphRouteFingerprint Route(string physicalNamespace) =>
        new()
        {
            ProjectionKind = "workflow-run-insight-graph",
            LogicalScope = "workflow-insights",
            OwnerId = "owner-alpha",
            PhysicalNamespace = physicalNamespace,
            RouteEpoch = 7,
            ContractId = "workflow-run-insight-graph-v2",
            ContractVersion = 2,
        };

    private static ProjectionGraphDelta Delta(
        ProjectionGraphRouteFingerprint route,
        long version,
        string eventId) =>
        new()
        {
            Route = route.Clone(),
            Source = new ProjectionGraphSourceCoordinate
            {
                ActorId = "actor-alpha",
                StateVersion = version,
                EventId = eventId,
            },
            Mode = ProjectionGraphDeltaMode.Normal,
        };

    private static ProjectionGraphDelta RepairDelta(
        ProjectionGraphRouteFingerprint route,
        long version,
        string eventId)
    {
        var delta = Delta(route, version, eventId);
        delta.Mode = ProjectionGraphDeltaMode.RepairOrCutover;
        return delta;
    }

    /// <summary>Minimal-payload nodes whose zero-padded ids sort ordinally, like snapshots do.</summary>
    private static IEnumerable<ProjectionGraphNodeMutation> Nodes(string prefix, int count) =>
        Enumerable.Range(0, count).Select(index => Node($"{prefix}-{index:D5}"));

    private static string[] NodeIds(ProjectionGraphOwnerSnapshotReadResult snapshot) =>
        snapshot.Snapshot.Nodes.Select(x => x.NodeId).ToArray();

    private static ProjectionGraphNodeMutation Node(
        string nodeId,
        params (string Key, string Value)[] properties)
    {
        var node = new ProjectionGraphNodeMutation
        {
            NodeId = nodeId,
            NodeType = "Step",
            UpdatedAtEpochMs = 1_700_000_000_000,
        };
        foreach (var (key, value) in properties)
            node.Properties[key] = value;
        return node;
    }

    private static ProjectionGraphEdgeMutation Edge(
        string edgeId,
        string fromNodeId,
        string toNodeId) =>
        new()
        {
            EdgeId = edgeId,
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            EdgeType = "NEXT",
            UpdatedAtEpochMs = 1_700_000_000_000,
        };

    internal static Neo4jProjectionGraphStoreOptions CreateNeo4jOptions(
        string nodeLabel,
        string edgeType) =>
        new()
        {
            Uri = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_URI"),
            Username = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME"),
            Password = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD"),
            Database = Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_DATABASE")?.Trim() ?? string.Empty,
            AutoCreateSchema = true,
            NodeLabel = nodeLabel,
            EdgeType = edgeType,
            RequestTimeoutMs = 30_000,
        };

    /// <summary>
    /// Name of the edge-identity owner seek index the store creates for <paramref name="nodeLabel"/>
    /// (schema names are normalized to lower case by the store).
    /// </summary>
    internal static string EdgeIdentityOwnerIndexName(string nodeLabel) =>
        $"projection_graph_v2_edge_identity_owner_{EdgeIdentityLabel(nodeLabel)}".ToLowerInvariant();

    internal static string EdgeIdentityLabel(string nodeLabel) => $"{nodeLabel}EdgeIdentity";

    internal static async Task CleanupNeo4jAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType)
    {
        var ownerStateLabel = $"{nodeLabel}OwnerState";
        var eventLabel = $"{nodeLabel}OwnerEvent";
        var edgeIdentityLabel = EdgeIdentityLabel(nodeLabel);
        var schemaNames = new[]
        {
            $"projection_graph_node_scope_owner_id_{nodeLabel}".ToLowerInvariant(),
            $"projection_graph_relationship_scope_owner_id_{edgeType}".ToLowerInvariant(),
            $"projection_graph_relationship_scope_edge_id_{edgeType}".ToLowerInvariant(),
            $"projection_graph_v2_pending_from_{edgeIdentityLabel}".ToLowerInvariant(),
            $"projection_graph_v2_pending_to_{edgeIdentityLabel}".ToLowerInvariant(),
            EdgeIdentityOwnerIndexName(nodeLabel),
        };
        var constraintNames = new[]
        {
            $"projection_graph_node_scope_id_{nodeLabel}".ToLowerInvariant(),
            $"projection_graph_v2_owner_{ownerStateLabel}".ToLowerInvariant(),
            $"projection_graph_v2_event_{eventLabel}".ToLowerInvariant(),
            $"projection_graph_v2_edge_{edgeIdentityLabel}".ToLowerInvariant(),
        };
        await using var session = driver.AsyncSession(options =>
        {
            options.WithDefaultAccessMode(AccessMode.Write);
            if (database.Length > 0)
                options.WithDatabase(database);
        });
        foreach (var cypher in new[]
                 {
                     $"MATCH (node:{nodeLabel}) DETACH DELETE node",
                     $"MATCH (node:{ownerStateLabel}) DETACH DELETE node",
                     $"MATCH (node:{eventLabel}) DETACH DELETE node",
                     $"MATCH (node:{edgeIdentityLabel}) DETACH DELETE node",
                 })
        {
            var cursor = await session.RunAsync(cypher);
            await cursor.ConsumeAsync();
        }

        foreach (var schemaName in schemaNames)
        {
            var cursor = await session.RunAsync($"DROP INDEX {schemaName} IF EXISTS");
            await cursor.ConsumeAsync();
        }

        foreach (var constraintName in constraintNames)
        {
            var cursor = await session.RunAsync($"DROP CONSTRAINT {constraintName} IF EXISTS");
            await cursor.ConsumeAsync();
        }
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Environment variable '{name}' is required.")
            : value.Trim();
    }
}
