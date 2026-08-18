using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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

    private static Neo4jProjectionGraphStoreOptions CreateNeo4jOptions(
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

    private static async Task CleanupNeo4jAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType)
    {
        var ownerStateLabel = $"{nodeLabel}OwnerState";
        var eventLabel = $"{nodeLabel}OwnerEvent";
        var edgeIdentityLabel = $"{nodeLabel}EdgeIdentity";
        var schemaNames = new[]
        {
            $"projection_graph_node_scope_owner_id_{nodeLabel}".ToLowerInvariant(),
            $"projection_graph_relationship_scope_owner_id_{edgeType}".ToLowerInvariant(),
            $"projection_graph_relationship_scope_edge_id_{edgeType}".ToLowerInvariant(),
            $"projection_graph_v2_pending_from_{edgeIdentityLabel}".ToLowerInvariant(),
            $"projection_graph_v2_pending_to_{edgeIdentityLabel}".ToLowerInvariant(),
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
