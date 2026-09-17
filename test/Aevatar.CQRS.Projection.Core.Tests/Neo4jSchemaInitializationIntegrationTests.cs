using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using FluentAssertions;
using Neo4j.Driver;
using Neo4jValueExtensions = Neo4j.Driver.ValueExtensions;

namespace Aevatar.CQRS.Projection.Core.Tests;

[Trait("Category", "ProviderIntegration")]
[Trait("Feature", "ProjectionProviders")]
public sealed class Neo4jSchemaInitializationIntegrationTests
{
    [Neo4jIntegrationFact]
    public async Task EnsureSchema_WhenEquivalentLegacyIndexesExist_ShouldWaitForTheirActualNames()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphNodeLegacy{suffix}";
        var edgeType = $"PROJECTION_REL_LEGACY_{suffix}";
        var legacyNodeIndexName = $"legacy_node_owner_{suffix}";
        var legacyRelationshipIndexName = $"legacy_relationship_owner_{suffix}";
        var preferredNodeIndexName = PreferredNodeIndexName(nodeLabel);
        var preferredRelationshipIndexName = PreferredRelationshipIndexName(edgeType);
        var constraintName = ConstraintName(nodeLabel);
        var options = CreateOptions(nodeLabel, edgeType);
        await using var driver = CreateDriver(options);
        var store = new Neo4jProjectionGraphStore(options);

        try
        {
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {legacyNodeIndexName} FOR (n:{nodeLabel}) " +
                "ON (n.scope, n.projectionOwnerId)");
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {legacyRelationshipIndexName} FOR ()-[r:{edgeType}]-() " +
                "ON (r.scope, r.projectionOwnerId)");
            await AwaitIndexAsync(driver, options.Database, legacyNodeIndexName);
            await AwaitIndexAsync(driver, options.Database, legacyRelationshipIndexName);

            var nodes = await store.ListNodesByOwnerAsync("legacy-scope", "legacy-owner");

            nodes.Should().BeEmpty();
            var indexes = await ReadIndexStatesAsync(
                driver,
                options.Database,
                legacyNodeIndexName,
                legacyRelationshipIndexName,
                preferredNodeIndexName,
                preferredRelationshipIndexName);
            indexes.Should().Contain(new KeyValuePair<string, string>(legacyNodeIndexName, "ONLINE"));
            indexes.Should().Contain(new KeyValuePair<string, string>(legacyRelationshipIndexName, "ONLINE"));
            indexes.Should().NotContainKey(preferredNodeIndexName);
            indexes.Should().NotContainKey(preferredRelationshipIndexName);
        }
        finally
        {
            await store.DisposeAsync();
            await CleanupSchemaAsync(
                driver,
                options.Database,
                nodeLabel,
                edgeType,
                constraintName,
                legacyNodeIndexName,
                legacyRelationshipIndexName,
                preferredNodeIndexName,
                preferredRelationshipIndexName);
        }
    }

    [Neo4jIntegrationFact]
    public async Task EnsureSchema_WhenPreferredNameHasWrongSchema_ShouldFailEvenWithEquivalentLegacyIndex()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphNodeConflict{suffix}";
        var edgeType = $"PROJECTION_REL_CONFLICT_{suffix}";
        var legacyNodeIndexName = $"legacy_node_owner_{suffix}";
        var legacyRelationshipIndexName = $"legacy_relationship_owner_{suffix}";
        var preferredNodeIndexName = PreferredNodeIndexName(nodeLabel);
        var preferredRelationshipIndexName = PreferredRelationshipIndexName(edgeType);
        var constraintName = ConstraintName(nodeLabel);
        var options = CreateOptions(nodeLabel, edgeType);
        await using var driver = CreateDriver(options);
        var store = new Neo4jProjectionGraphStore(options);

        try
        {
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {preferredNodeIndexName} FOR (n:{nodeLabel}) ON (n.scope)");
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {legacyNodeIndexName} FOR (n:{nodeLabel}) " +
                "ON (n.scope, n.projectionOwnerId)");
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {legacyRelationshipIndexName} FOR ()-[r:{edgeType}]-() " +
                "ON (r.scope, r.projectionOwnerId)");
            await AwaitIndexAsync(driver, options.Database, preferredNodeIndexName);
            await AwaitIndexAsync(driver, options.Database, legacyNodeIndexName);
            await AwaitIndexAsync(driver, options.Database, legacyRelationshipIndexName);

            Func<Task> act = () => store.ListNodesByOwnerAsync("conflict-scope", "conflict-owner");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{preferredNodeIndexName}*occupied by a non-equivalent schema*");
        }
        finally
        {
            await store.DisposeAsync();
            await CleanupSchemaAsync(
                driver,
                options.Database,
                nodeLabel,
                edgeType,
                constraintName,
                preferredNodeIndexName,
                preferredRelationshipIndexName,
                legacyNodeIndexName,
                legacyRelationshipIndexName);
        }
    }

    [Neo4jIntegrationFact]
    public async Task EnsureSchema_WhenEdgeIdIndexNameHasWrongSchema_ShouldFailClosed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeLabel = $"ProjectionGraphNodeEdgeIdConflict{suffix}";
        var edgeType = $"PROJECTION_REL_EDGEID_CONFLICT_{suffix}";
        var preferredEdgeIdIndexName = PreferredRelationshipEdgeIdIndexName(edgeType);
        var constraintName = ConstraintName(nodeLabel);
        var options = CreateOptions(nodeLabel, edgeType);
        await using var driver = CreateDriver(options);
        var store = new Neo4jProjectionGraphStore(options);

        try
        {
            // Squat the preferred edge-id index name with a non-equivalent schema; the store
            // must fail closed instead of awaiting the squatter and serving scans.
            await ExecuteAsync(
                driver,
                options.Database,
                $"CREATE RANGE INDEX {preferredEdgeIdIndexName} FOR ()-[r:{edgeType}]-() ON (r.scope)");
            await AwaitIndexAsync(driver, options.Database, preferredEdgeIdIndexName);

            Func<Task> act = () => store.ListNodesByOwnerAsync("edgeid-scope", "edgeid-owner");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{preferredEdgeIdIndexName}*occupied by a non-equivalent schema*");
        }
        finally
        {
            await store.DisposeAsync();
            await CleanupSchemaAsync(
                driver,
                options.Database,
                nodeLabel,
                edgeType,
                constraintName,
                preferredEdgeIdIndexName);
        }
    }

    private static Neo4jProjectionGraphStoreOptions CreateOptions(string nodeLabel, string edgeType)
    {
        return new Neo4jProjectionGraphStoreOptions
        {
            Uri = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_URI"),
            Username = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_USERNAME"),
            Password = GetRequiredEnvironmentVariable("AEVATAR_TEST_NEO4J_PASSWORD"),
            Database = Environment.GetEnvironmentVariable("AEVATAR_TEST_NEO4J_DATABASE")?.Trim() ?? "",
            AutoCreateSchema = true,
            NodeLabel = nodeLabel,
            EdgeType = edgeType,
            RequestTimeoutMs = 30000,
        };
    }

    private static IDriver CreateDriver(Neo4jProjectionGraphStoreOptions options)
    {
        return GraphDatabase.Driver(
            options.Uri,
            AuthTokens.Basic(options.Username, options.Password));
    }

    private static async Task ExecuteAsync(IDriver driver, string database, string cypher)
    {
        await using var session = CreateSession(driver, database, AccessMode.Write);
        var cursor = await session.RunAsync(cypher);
        await cursor.ConsumeAsync();
    }

    private static async Task AwaitIndexAsync(IDriver driver, string database, string indexName)
    {
        await using var session = CreateSession(driver, database, AccessMode.Write);
        var cursor = await session.RunAsync(
            "CALL db.awaitIndex($indexName, $timeoutSeconds)",
            new Dictionary<string, object?>
            {
                ["indexName"] = indexName,
                ["timeoutSeconds"] = 30L,
            });
        await cursor.ConsumeAsync();
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadIndexStatesAsync(
        IDriver driver,
        string database,
        params string[] indexNames)
    {
        await using var session = CreateSession(driver, database, AccessMode.Read);
        var cursor = await session.RunAsync(
            "SHOW INDEXES YIELD name, state WHERE name IN $indexNames RETURN name, state",
            new Dictionary<string, object?> { ["indexNames"] = indexNames });
        var rows = await cursor.ToListAsync(record => new KeyValuePair<string, string>(
            Neo4jValueExtensions.As<string>(record["name"]),
            Neo4jValueExtensions.As<string>(record["state"])));
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    private static async Task CleanupSchemaAsync(
        IDriver driver,
        string database,
        string nodeLabel,
        string edgeType,
        string constraintName,
        params string[] indexNames)
    {
        await using var session = CreateSession(driver, database, AccessMode.Write);
        var edgeIdentityLabel = $"{nodeLabel}EdgeIdentity";
        var versionedIndexNames = new[]
        {
            $"projection_graph_v2_pending_from_{edgeIdentityLabel}".ToLowerInvariant(),
            $"projection_graph_v2_pending_to_{edgeIdentityLabel}".ToLowerInvariant(),
            $"projection_graph_relationship_scope_edge_id_{edgeType}".ToLowerInvariant(),
            $"projection_graph_v2_edge_identity_owner_{edgeIdentityLabel}".ToLowerInvariant(),
        };
        foreach (var indexName in indexNames
                     .Concat(versionedIndexNames)
                     .Distinct(StringComparer.Ordinal))
        {
            var indexCursor = await session.RunAsync($"DROP INDEX {indexName} IF EXISTS");
            await indexCursor.ConsumeAsync();
        }

        var versionedConstraintNames = new[]
        {
            $"projection_graph_v2_owner_{nodeLabel}OwnerState".ToLowerInvariant(),
            $"projection_graph_v2_event_{nodeLabel}OwnerEvent".ToLowerInvariant(),
            $"projection_graph_v2_edge_{edgeIdentityLabel}".ToLowerInvariant(),
        };
        foreach (var name in versionedConstraintNames.Append(constraintName))
        {
            var constraintCursor = await session.RunAsync($"DROP CONSTRAINT {name} IF EXISTS");
            await constraintCursor.ConsumeAsync();
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

    private static string PreferredNodeIndexName(string nodeLabel) =>
        $"projection_graph_node_scope_owner_id_{nodeLabel}".ToLowerInvariant();

    private static string PreferredRelationshipIndexName(string edgeType) =>
        $"projection_graph_relationship_scope_owner_id_{edgeType}".ToLowerInvariant();

    private static string PreferredRelationshipEdgeIdIndexName(string edgeType) =>
        $"projection_graph_relationship_scope_edge_id_{edgeType}".ToLowerInvariant();

    private static string ConstraintName(string nodeLabel) =>
        $"projection_graph_node_scope_id_{nodeLabel}".ToLowerInvariant();

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        throw new InvalidOperationException($"Environment variable '{name}' is required.");
    }
}
