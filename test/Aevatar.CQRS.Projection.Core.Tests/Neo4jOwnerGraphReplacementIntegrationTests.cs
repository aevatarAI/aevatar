using System.Reflection;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Neo4j.Driver;
using Neo4jValueExtensions = Neo4j.Driver.ValueExtensions;

namespace Aevatar.CQRS.Projection.Core.Tests;

[Trait("Category", "ProviderIntegration")]
[Trait("Feature", "ProjectionProviders")]
public sealed class Neo4jOwnerGraphReplacementIntegrationTests
{
    [Neo4jIntegrationFact]
    public async Task ReplaceOwnerGraph_ShouldUseOwnerIndexesOnceAndPreserveOtherOwners()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphNodeE2E{suffix}";
        var edgeType = $"PROJECTION_REL_E2E_{suffix}";
        var scope = $"owner-replacement-e2e-{suffix}";
        var targetOwner = $"target-{suffix}";
        var otherOwner = $"other-{suffix}";
        var nodeIndexName = $"projection_graph_node_scope_owner_id_{nodeLabel}".ToLowerInvariant();
        var relationshipIndexName =
            $"projection_graph_relationship_scope_owner_id_{edgeType}".ToLowerInvariant();
        var constraintName = $"projection_graph_node_scope_id_{nodeLabel}".ToLowerInvariant();
        var database = Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_DATABASE")?.Trim() ?? "";
        var options = new Neo4jProjectionGraphStoreOptions
        {
            Uri = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_URI"),
            Username = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME"),
            Password = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD"),
            Database = database,
            AutoCreateSchema = true,
            NodeLabel = nodeLabel,
            EdgeType = edgeType,
            RequestTimeoutMs = 30000,
        };
        var auth = AuthTokens.Basic(options.Username, options.Password);
        await using var driver = GraphDatabase.Driver(options.Uri, auth);
        var store = new Neo4jProjectionGraphStore(options);

        try
        {
            var oldTargetGraph = CreateGraph(
                scope,
                targetOwner,
                1,
                ["target-a", "target-b", "target-c", "target-d"],
                [
                    new EdgeSpec("old-edge-1", "target-a", "target-b"),
                    new EdgeSpec("old-edge-2", "target-b", "target-c"),
                    new EdgeSpec("old-edge-3", "target-c", "target-d"),
                ]);
            var replacementGraph = CreateGraph(
                scope,
                targetOwner,
                2,
                ["target-a", "target-b", "target-c"],
                [
                    new EdgeSpec("new-edge-1", "target-a", "target-b"),
                    new EdgeSpec("new-edge-2", "target-b", "target-c"),
                ]);
            var otherGraph = CreateGraph(
                scope,
                otherOwner,
                1,
                ["other-a", "other-b"],
                [new EdgeSpec("other-edge", "other-a", "other-b")]);

            await SeedGraphAsync(driver, database, nodeLabel, edgeType, oldTargetGraph);
            await SeedGraphAsync(driver, database, nodeLabel, edgeType, otherGraph);
            await SeedUnrelatedOwnersAsync(driver, database, nodeLabel, edgeType, scope, 1, 100);

            await AssertOldTargetGraphRemainsAsync(store, scope, targetOwner);

            var indexes = await ReadOwnerIndexesAsync(
                driver,
                database,
                nodeIndexName,
                relationshipIndexName);
            indexes.Should().HaveCount(2);
            AssertIndex(
                indexes[nodeIndexName],
                "NODE",
                nodeLabel);
            AssertIndex(
                indexes[relationshipIndexName],
                "RELATIONSHIP",
                edgeType);

            var profileAt100 = await ProfileReplacementAsync(
                driver,
                database,
                nodeLabel,
                edgeType,
                replacementGraph);
            await AssertOldTargetGraphRemainsAsync(store, scope, targetOwner);

            await SeedUnrelatedOwnersAsync(driver, database, nodeLabel, edgeType, scope, 101, 1000);
            var profileAt1000 = await ProfileReplacementAsync(
                driver,
                database,
                nodeLabel,
                edgeType,
                replacementGraph);
            await AssertOldTargetGraphRemainsAsync(store, scope, targetOwner);

            profileAt100.RelationshipsDeleted.Should().Be(3);
            profileAt100.PropertiesSet.Should().Be(29);
            profileAt1000.RelationshipsDeleted.Should().Be(3);
            profileAt1000.PropertiesSet.Should().Be(29);
            profileAt1000.Operators.Should().Contain(operatorName =>
                operatorName.Contains("RelationshipIndexSeek", StringComparison.Ordinal),
                "the plan operators were {0}",
                string.Join(", ", profileAt1000.Operators));
            profileAt1000.Operators.Should().Contain(operatorName =>
                operatorName.Contains("NodeIndexSeek", StringComparison.Ordinal),
                "the plan operators were {0}",
                string.Join(", ", profileAt1000.Operators));
            profileAt1000.Operators.Should().NotContain(operatorName =>
                operatorName.Contains("RelationshipTypeScan", StringComparison.Ordinal),
                "the plan operators were {0}",
                string.Join(", ", profileAt1000.Operators));
            profileAt1000.Operators.Should().NotContain(operatorName =>
                operatorName.Contains("NodeByLabelScan", StringComparison.Ordinal),
                "the plan operators were {0}",
                string.Join(", ", profileAt1000.Operators));
            profileAt1000.DbHits.Should().BeLessThanOrEqualTo(
                profileAt100.DbHits * 2,
                "the plan operators were {0}",
                string.Join(", ", profileAt1000.Operators));

            await store.ReplaceOwnerGraphAsync(replacementGraph);

            var targetNodes = await store.ListNodesByOwnerAsync(scope, targetOwner);
            var targetEdges = await store.ListEdgesByOwnerAsync(scope, targetOwner);
            targetNodes.Select(node => node.NodeId).Should().BeEquivalentTo(
                "target-a",
                "target-b",
                "target-c");
            targetEdges.Select(edge => edge.EdgeId).Should().BeEquivalentTo(
                "new-edge-1",
                "new-edge-2");

            var otherNodes = await store.ListNodesByOwnerAsync(scope, otherOwner);
            var otherEdges = await store.ListEdgesByOwnerAsync(scope, otherOwner);
            otherNodes.Select(node => node.NodeId).Should().BeEquivalentTo("other-a", "other-b");
            otherEdges.Select(edge => edge.EdgeId).Should().ContainSingle().Which.Should().Be("other-edge");
        }
        finally
        {
            await store.DisposeAsync();
            await CleanupAsync(
                driver,
                database,
                nodeLabel,
                constraintName,
                nodeIndexName,
                relationshipIndexName);
        }
    }

    private static ProjectionOwnedGraph CreateGraph(
        string scope,
        string ownerId,
        long stateVersion,
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<EdgeSpec> edges)
    {
        return new ProjectionOwnedGraph
        {
            ProjectionKind = "neo4j-owner-replacement-integration",
            StateVersion = stateVersion,
            Scope = scope,
            OwnerId = ownerId,
            Nodes = nodeIds.Select(nodeId => new ProjectionGraphNode
            {
                Scope = scope,
                NodeId = nodeId,
                NodeType = "TestNode",
                Properties = ManagedProperties(ownerId),
                UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(stateVersion),
            }).ToArray(),
            Edges = edges.Select(edge => new ProjectionGraphEdge
            {
                Scope = scope,
                EdgeId = edge.EdgeId,
                FromNodeId = edge.FromNodeId,
                ToNodeId = edge.ToNodeId,
                EdgeType = "LINK",
                Properties = ManagedProperties(ownerId),
                UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(stateVersion),
            }).ToArray(),
        };
    }

    private static Dictionary<string, string> ManagedProperties(string ownerId)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectionGraphManagedPropertyKeys.ManagedMarkerKey] =
                ProjectionGraphManagedPropertyKeys.ManagedMarkerValue,
            [ProjectionGraphManagedPropertyKeys.ManagedOwnerIdKey] = ownerId,
        };
    }

    private static async Task<IReadOnlyDictionary<string, IndexDescription>> ReadOwnerIndexesAsync(
        IDriver driver,
        string database,
        string nodeIndexName,
        string relationshipIndexName)
    {
        await using var session = CreateSession(driver, database, AccessMode.Read);
        var cursor = await session.RunAsync(
            "SHOW INDEXES YIELD name, type, entityType, labelsOrTypes, properties, state " +
            "WHERE name IN $indexNames " +
            "RETURN name, type, entityType, labelsOrTypes, properties, state",
            new Dictionary<string, object?>
            {
                ["indexNames"] = new[] { nodeIndexName, relationshipIndexName },
            });
        var rows = await cursor.ToListAsync(record => new IndexDescription(
            Neo4jValueExtensions.As<string>(record["name"]),
            Neo4jValueExtensions.As<string>(record["type"]),
            Neo4jValueExtensions.As<string>(record["entityType"]),
            Neo4jValueExtensions.As<List<string>>(record["labelsOrTypes"]),
            Neo4jValueExtensions.As<List<string>>(record["properties"]),
            Neo4jValueExtensions.As<string>(record["state"])));
        return rows.ToDictionary(row => row.Name, StringComparer.Ordinal);
    }

    private static void AssertIndex(
        IndexDescription index,
        string expectedEntityType,
        string expectedLabelOrType)
    {
        index.Type.Should().Be("RANGE");
        index.EntityType.Should().Be(expectedEntityType);
        index.LabelsOrTypes.Should().Equal(expectedLabelOrType);
        index.Properties.Should().Equal("scope", "projectionOwnerId");
        index.State.Should().Be("ONLINE");
    }

    private static async Task SeedUnrelatedOwnersAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType,
        string scope,
        int start,
        int end)
    {
        var cypher =
            $"UNWIND range($start, $end) AS ownerIndex " +
            "WITH ownerIndex, 'unrelated-' + toString(ownerIndex) AS ownerId " +
            $"CREATE (from:{nodeLabel} {{scope: $scope, nodeId: ownerId + '-a', nodeType: 'TestNode', " +
            "propertiesJson: '{}', updatedAtEpochMs: 0, projectionManaged: true, projectionOwnerId: ownerId}) " +
            $"CREATE (to:{nodeLabel} {{scope: $scope, nodeId: ownerId + '-b', nodeType: 'TestNode', " +
            "propertiesJson: '{}', updatedAtEpochMs: 0, projectionManaged: true, projectionOwnerId: ownerId}) " +
            $"CREATE (from)-[:{edgeType} {{scope: $scope, edgeId: ownerId + '-edge', relationType: 'LINK', " +
            "propertiesJson: '{}', updatedAtEpochMs: 0, projectionManaged: true, projectionOwnerId: ownerId}]->(to)";
        await using var session = CreateSession(driver, database, AccessMode.Write);
        var cursor = await session.RunAsync(
            cypher,
            new Dictionary<string, object?>
            {
                ["start"] = start,
                ["end"] = end,
                ["scope"] = scope,
            });
        await cursor.ConsumeAsync();
    }

    private static async Task SeedGraphAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType,
        ProjectionOwnedGraph graph)
    {
        var parameters = BuildReplacementParameters(graph);
        await using var session = CreateSession(driver, database, AccessMode.Write);
        await using var transaction = await session.BeginTransactionAsync();
        var nodeCursor = await transaction.RunAsync(
            "UNWIND $nodes AS node " +
            $"CREATE (n:{nodeLabel} {{scope: $scope, nodeId: node.nodeId, nodeType: node.nodeType, " +
            "propertiesJson: node.propertiesJson, updatedAtEpochMs: node.updatedAtEpochMs, " +
            "projectionManaged: node.projectionManaged, projectionOwnerId: node.projectionOwnerId})",
            parameters);
        await nodeCursor.ConsumeAsync();
        var edgeCursor = await transaction.RunAsync(
            "UNWIND $edges AS edge " +
            $"MATCH (from:{nodeLabel} {{scope: $scope, nodeId: edge.fromNodeId}}) " +
            $"MATCH (to:{nodeLabel} {{scope: $scope, nodeId: edge.toNodeId}}) " +
            $"CREATE (from)-[r:{edgeType} {{scope: $scope, edgeId: edge.edgeId, relationType: edge.relationType, " +
            "propertiesJson: edge.propertiesJson, updatedAtEpochMs: edge.updatedAtEpochMs, " +
            "projectionManaged: edge.projectionManaged, projectionOwnerId: edge.projectionOwnerId}]->(to)",
            parameters);
        await edgeCursor.ConsumeAsync();
        await transaction.CommitAsync();
    }

    private static async Task<ReplacementProfile> ProfileReplacementAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType,
        ProjectionOwnedGraph graph)
    {
        var cypherSupportType = typeof(Neo4jProjectionGraphStore).Assembly.GetType(
            "Aevatar.CQRS.Projection.Providers.Neo4j.Stores.Neo4jProjectionGraphStoreCypherSupport",
            throwOnError: true)!;
        var buildMethod = cypherSupportType.GetMethod(
            "BuildReplaceOwnerGraphCypher",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var cypher = (string)buildMethod.Invoke(null, [nodeLabel, edgeType])!;
        var parameters = BuildReplacementParameters(graph);

        await using var session = CreateSession(driver, database, AccessMode.Write);
        await using var transaction = await session.BeginTransactionAsync();
        var cursor = await transaction.RunAsync("PROFILE " + cypher, parameters);
        var summary = await cursor.ConsumeAsync();
        var profile = summary.Profile;
        profile.Should().NotBeNull();
        var operators = FlattenProfile(profile!).Select(item => item.OperatorType).ToArray();
        var dbHits = FlattenProfile(profile!).Sum(item => item.DbHits);
        var result = new ReplacementProfile(
            dbHits,
            summary.Counters.RelationshipsDeleted,
            summary.Counters.PropertiesSet,
            operators);
        await transaction.RollbackAsync();
        return result;
    }

    private static IReadOnlyDictionary<string, object?> BuildReplacementParameters(ProjectionOwnedGraph graph)
    {
        var nodes = graph.Nodes.Select(node => new Dictionary<string, object?>
        {
            ["nodeId"] = node.NodeId,
            ["nodeType"] = node.NodeType,
            ["propertiesJson"] = JsonSerializer.Serialize(node.Properties),
            ["updatedAtEpochMs"] = node.UpdatedAt.ToUnixTimeMilliseconds(),
            ["projectionManaged"] = true,
            ["projectionOwnerId"] = graph.OwnerId,
        }).ToArray();
        var edges = graph.Edges.Select(edge => new Dictionary<string, object?>
        {
            ["edgeId"] = edge.EdgeId,
            ["fromNodeId"] = edge.FromNodeId,
            ["toNodeId"] = edge.ToNodeId,
            ["relationType"] = edge.EdgeType,
            ["propertiesJson"] = JsonSerializer.Serialize(edge.Properties),
            ["updatedAtEpochMs"] = edge.UpdatedAt.ToUnixTimeMilliseconds(),
            ["projectionManaged"] = true,
            ["projectionOwnerId"] = graph.OwnerId,
        }).ToArray();
        return new Dictionary<string, object?>
        {
            ["scope"] = graph.Scope,
            ["ownerId"] = graph.OwnerId,
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["targetNodeIds"] = graph.Nodes.Select(node => node.NodeId).ToArray(),
        };
    }

    private static IEnumerable<IProfiledPlan> FlattenProfile(IProfiledPlan root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var descendant in FlattenProfile(child))
                yield return descendant;
        }
    }

    private static async Task AssertOldTargetGraphRemainsAsync(
        Neo4jProjectionGraphStore store,
        string scope,
        string targetOwner)
    {
        var targetNodes = await store.ListNodesByOwnerAsync(scope, targetOwner);
        var targetEdges = await store.ListEdgesByOwnerAsync(scope, targetOwner);
        targetNodes.Select(node => node.NodeId).Should().BeEquivalentTo(
            "target-a",
            "target-b",
            "target-c",
            "target-d");
        targetEdges.Select(edge => edge.EdgeId).Should().BeEquivalentTo(
            "old-edge-1",
            "old-edge-2",
            "old-edge-3");
    }

    private static async Task CleanupAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string constraintName,
        string nodeIndexName,
        string relationshipIndexName)
    {
        await using var session = CreateSession(driver, database, AccessMode.Write);
        var ownerStateLabel = $"{nodeLabel}OwnerState";
        var eventLabel = $"{nodeLabel}OwnerEvent";
        var edgeIdentityLabel = $"{nodeLabel}EdgeIdentity";
        var pendingFromIndexName =
            $"projection_graph_v2_pending_from_{edgeIdentityLabel}".ToLowerInvariant();
        var pendingToIndexName =
            $"projection_graph_v2_pending_to_{edgeIdentityLabel}".ToLowerInvariant();
        var edgeIdentityOwnerIndexName =
            $"projection_graph_v2_edge_identity_owner_{edgeIdentityLabel}".ToLowerInvariant();
        var versionedOwnerConstraintName =
            $"projection_graph_v2_owner_{ownerStateLabel}".ToLowerInvariant();
        var versionedEventConstraintName =
            $"projection_graph_v2_event_{eventLabel}".ToLowerInvariant();
        var versionedEdgeConstraintName =
            $"projection_graph_v2_edge_{edgeIdentityLabel}".ToLowerInvariant();
        foreach (var cypher in new[]
                 {
                     $"MATCH (n:{nodeLabel}) DETACH DELETE n",
                     $"MATCH (n:{ownerStateLabel}) DETACH DELETE n",
                     $"MATCH (n:{eventLabel}) DETACH DELETE n",
                     $"MATCH (n:{edgeIdentityLabel}) DETACH DELETE n",
                     $"DROP INDEX {nodeIndexName} IF EXISTS",
                     $"DROP INDEX {relationshipIndexName} IF EXISTS",
                     $"DROP INDEX {edgeIdentityOwnerIndexName} IF EXISTS",
                     $"DROP INDEX {pendingFromIndexName} IF EXISTS",
                     $"DROP INDEX {pendingToIndexName} IF EXISTS",
                     $"DROP CONSTRAINT {constraintName} IF EXISTS",
                     $"DROP CONSTRAINT {versionedOwnerConstraintName} IF EXISTS",
                     $"DROP CONSTRAINT {versionedEventConstraintName} IF EXISTS",
                     $"DROP CONSTRAINT {versionedEdgeConstraintName} IF EXISTS",
                 })
        {
            var cursor = await session.RunAsync(cypher);
            await cursor.ConsumeAsync();
        }
    }

    private static IAsyncSession CreateSession(IDriver driver, string database, AccessMode accessMode)
    {
        return driver.AsyncSession(options =>
        {
            options.WithDefaultAccessMode(accessMode);
            if (database.Length > 0)
                options.WithDatabase(database);
        });
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }

    private sealed record IndexDescription(
        string Name,
        string Type,
        string EntityType,
        IReadOnlyList<string> LabelsOrTypes,
        IReadOnlyList<string> Properties,
        string State);

    private sealed record ReplacementProfile(
        long DbHits,
        int RelationshipsDeleted,
        int PropertiesSet,
        IReadOnlyList<string> Operators);

    private sealed record EdgeSpec(string EdgeId, string FromNodeId, string ToNodeId);
}
