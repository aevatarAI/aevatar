using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed partial class Neo4jProjectionGraphStore
{
    private const long SchemaIndexAwaitCycleSeconds = 5;
    private static readonly TimeSpan SchemaIndexAwaitTimeout = TimeSpan.FromSeconds(300);

    private async Task<List<ProjectionGraphNode>> GetNodesByIdsAsync(
        string scope,
        IReadOnlySet<string> nodeIds,
        CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return [];

        await EnsureSchemaAsync(ct);
        var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildGetNodesByIdsCypher(_nodeLabel);
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["nodeIds"] = nodeIds.ToArray(),
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var nodes = new List<ProjectionGraphNode>(rows.Count);
        foreach (var row in rows)
        {
            var node = Neo4jProjectionGraphStoreRowMapper.MapNode(scope, row, DeserializeProperties);
            if (node != null)
                nodes.Add(node);
        }

        return nodes;
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (!_autoCreateSchema || _schemaInitialized)
            return;

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaInitialized)
                return;

            var nodeConstraintName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_node_scope_id_{_nodeLabel}");
            var nodeOwnerIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_node_scope_owner_id_{_nodeLabel}");
            var relationshipOwnerIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_relationship_scope_owner_id_{_edgeType}");
            var relationshipEdgeIdIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_relationship_scope_edge_id_{_edgeType}");
            var versionedOwnerConstraintName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_owner_{_versionedOwnerStateLabel}");
            var versionedEventConstraintName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_event_{_versionedEventLabel}");
            var versionedEdgeConstraintName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_edge_{_versionedEdgeIdentityLabel}");
            var pendingFromIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_pending_from_{_versionedEdgeIdentityLabel}");
            var pendingToIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_pending_to_{_versionedEdgeIdentityLabel}");
            var edgeIdentityOwnerIndexName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeSchemaName(
                $"projection_graph_v2_edge_identity_owner_{_versionedEdgeIdentityLabel}");

            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreCypherSupport.BuildCreateNodeConstraintCypher(
                    _nodeLabel,
                    nodeConstraintName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreCypherSupport.BuildCreateNodeOwnerIndexCypher(
                    _nodeLabel,
                    nodeOwnerIndexName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreCypherSupport.BuildCreateRelationshipOwnerIndexCypher(
                    _edgeType,
                    relationshipOwnerIndexName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateRelationshipEdgeIdIndexCypher(
                    _edgeType,
                    relationshipEdgeIdIndexName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateOwnerStateConstraintCypher(
                    _versionedOwnerStateLabel,
                    versionedOwnerConstraintName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEventConstraintCypher(
                    _versionedEventLabel,
                    versionedEventConstraintName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEdgeIdentityConstraintCypher(
                    _versionedEdgeIdentityLabel,
                    versionedEdgeConstraintName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreatePendingFromIndexCypher(
                    _versionedEdgeIdentityLabel,
                    pendingFromIndexName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreatePendingToIndexCypher(
                    _versionedEdgeIdentityLabel,
                    pendingToIndexName),
                new Dictionary<string, object?>(),
                ct);
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreVersionedCypherSupport.BuildCreateEdgeIdentityOwnerIndexCypher(
                    _versionedEdgeIdentityLabel,
                    edgeIdentityOwnerIndexName),
                new Dictionary<string, object?>(),
                ct);

            var indexes = await ReadSchemaIndexesAsync(null, ct);
            var actualNodeOwnerIndexName = ResolveRequiredIndexName(
                indexes,
                nodeOwnerIndexName,
                "NODE",
                _nodeLabel,
                ["scope", "projectionOwnerId"]);
            var actualRelationshipOwnerIndexName = ResolveRequiredIndexName(
                indexes,
                relationshipOwnerIndexName,
                "RELATIONSHIP",
                _edgeType,
                ["scope", "projectionOwnerId"]);
            var actualRelationshipEdgeIdIndexName = ResolveRequiredIndexName(
                indexes,
                relationshipEdgeIdIndexName,
                "RELATIONSHIP",
                _edgeType,
                ["scope", "edgeId"]);
            var actualEdgeIdentityOwnerIndexName = ResolveRequiredIndexName(
                indexes,
                edgeIdentityOwnerIndexName,
                "NODE",
                _versionedEdgeIdentityLabel,
                ["physicalNamespace", "projectionOwnerId"]);

            await AwaitIndexAsync(actualNodeOwnerIndexName, ct);
            await AwaitIndexAsync(actualRelationshipOwnerIndexName, ct);
            await AwaitIndexAsync(actualRelationshipEdgeIdIndexName, ct);
            await AwaitIndexAsync(actualEdgeIdentityOwnerIndexName, ct);
            _schemaInitialized = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private Task AwaitIndexAsync(string indexName, CancellationToken ct)
    {
        return Neo4jProjectionGraphStoreSchemaWaitSupport.WaitAsync(
            (cycleSeconds, cycleCt) => TryAwaitIndexCycleAsync(indexName, cycleSeconds, cycleCt),
            SchemaIndexAwaitTimeout,
            SchemaIndexAwaitCycleSeconds,
            TimeProvider.System,
            ct);
    }

    private async Task<bool> TryAwaitIndexCycleAsync(
        string indexName,
        long cycleSeconds,
        CancellationToken ct)
    {
        var indexes = await ReadSchemaIndexesAsync(indexName, ct);
        var index = indexes.SingleOrDefault();
        if (index == null)
        {
            throw new InvalidOperationException(
                $"Neo4j schema index '{indexName}' disappeared while waiting for it to become ONLINE.");
        }

        if (string.Equals(index.State, "FAILED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Neo4j schema index '{indexName}' entered FAILED state: {index.FailureMessage}");
        }

        if (!string.Equals(index.State, "ONLINE", StringComparison.Ordinal) &&
            !string.Equals(index.State, "POPULATING", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Neo4j schema index '{indexName}' reported unsupported state '{index.State}'.");
        }

        try
        {
            await ExecuteWriteAsync(
                Neo4jProjectionGraphStoreCypherSupport.BuildAwaitIndexCypher(),
                new Dictionary<string, object?>
                {
                    ["indexName"] = indexName,
                    ["timeoutSeconds"] = cycleSeconds,
                },
                ct);
            return true;
        }
        catch (Neo4jException exception) when (IsIndexAwaitTimeout(exception))
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private async Task<IReadOnlyList<SchemaIndexDescription>> ReadSchemaIndexesAsync(
        string? indexName,
        CancellationToken ct)
    {
        var byName = indexName != null;
        var cypher = byName
            ? Neo4jProjectionGraphStoreCypherSupport.BuildShowIndexByNameCypher()
            : Neo4jProjectionGraphStoreCypherSupport.BuildShowIndexesCypher();
        var parameters = byName
            ? new Dictionary<string, object?> { ["indexName"] = indexName }
            : new Dictionary<string, object?>();
        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        return rows.Select(row => new SchemaIndexDescription(
                ReadString(row, "name"),
                ReadString(row, "type"),
                ReadString(row, "entityType"),
                ReadStringList(row, "labelsOrTypes"),
                ReadStringList(row, "properties"),
                ReadString(row, "state"),
                ReadString(row, "failureMessage")))
            .ToArray();
    }

    private static string ResolveRequiredIndexName(
        IReadOnlyList<SchemaIndexDescription> indexes,
        string preferredName,
        string entityType,
        string labelOrType,
        IReadOnlyList<string> properties)
    {
        var preferredIndex = indexes.FirstOrDefault(index =>
            string.Equals(index.Name, preferredName, StringComparison.Ordinal));
        if (preferredIndex != null)
        {
            if (IsEquivalentRangeIndex(preferredIndex, entityType, labelOrType, properties))
                return preferredIndex.Name;

            throw new InvalidOperationException(
                $"Neo4j schema index name '{preferredName}' is occupied by a non-equivalent schema. " +
                $"Expected RANGE {entityType} index on {labelOrType}({string.Join(", ", properties)}).");
        }

        var equivalentIndexes = indexes
            .Where(index => IsEquivalentRangeIndex(index, entityType, labelOrType, properties))
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .ToArray();
        if (equivalentIndexes.Length > 0)
            return equivalentIndexes[0].Name;

        throw new InvalidOperationException(
            $"Neo4j did not expose an equivalent RANGE {entityType} index for " +
            $"{labelOrType}({string.Join(", ", properties)}) after schema initialization.");
    }

    private static bool IsEquivalentRangeIndex(
        SchemaIndexDescription index,
        string entityType,
        string labelOrType,
        IReadOnlyList<string> properties)
    {
        return string.Equals(index.Type, "RANGE", StringComparison.Ordinal) &&
               string.Equals(index.EntityType, entityType, StringComparison.Ordinal) &&
               index.LabelsOrTypes.SequenceEqual([labelOrType], StringComparer.Ordinal) &&
               index.Properties.SequenceEqual(properties, StringComparer.Ordinal);
    }

    private static bool IsIndexAwaitTimeout(Neo4jException exception)
    {
        return string.Equals(
                   exception.Code,
                   "Neo.ClientError.Procedure.ProcedureCallFailed",
                   StringComparison.Ordinal) &&
               exception.Message.Contains("did not come online within", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(IReadOnlyDictionary<string, object> row, string key)
    {
        return row.TryGetValue(key, out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            : "";
    }

    private static IReadOnlyList<string> ReadStringList(
        IReadOnlyDictionary<string, object> row,
        string key)
    {
        if (!row.TryGetValue(key, out var value) || value is not IEnumerable<object> items)
            return [];

        return items
            .Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) ?? "")
            .ToArray();
    }

    private async Task ExecuteWriteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = CreateSession(AccessMode.Write);
        var cursor = await session.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        // No post-consume cancellation check: a consumed auto-commit write is already
        // durable on the server, and throwing here would mislabel it as cancelled.
    }

    private async Task ExecuteWriteTransactionAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var session = CreateSession(AccessMode.Write);
        await using var transaction = await session.BeginTransactionAsync();
        var cursor = await transaction.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync();
        // No post-commit cancellation check: the transaction is durable once committed,
        // and throwing here would mislabel the committed write as cancelled.
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ExecuteReadAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Read);
        var cursor = await session.RunAsync(cypher, parameters);
        var rows = await cursor.ToListAsync(record =>
            (IReadOnlyDictionary<string, object>)record.Values.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal));
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

    private string SerializeProperties(IReadOnlyDictionary<string, string> properties)
    {
        return Neo4jProjectionGraphStorePropertyCodec.SerializeProperties(properties, _jsonOptions);
    }

    private Dictionary<string, string> DeserializeProperties(string payload)
    {
        return Neo4jProjectionGraphStorePropertyCodec.DeserializeProperties(payload, _jsonOptions, _logger, ProviderName);
    }

    private sealed record SchemaIndexDescription(
        string Name,
        string Type,
        string EntityType,
        IReadOnlyList<string> LabelsOrTypes,
        IReadOnlyList<string> Properties,
        string State,
        string FailureMessage);
}
