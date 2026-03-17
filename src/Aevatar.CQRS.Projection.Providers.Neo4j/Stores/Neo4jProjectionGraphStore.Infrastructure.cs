using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed partial class Neo4jProjectionGraphStore
{
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
        if (!_autoCreateConstraints || _schemaInitialized)
            return;

        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaInitialized)
                return;

            var nodeConstraintName = Neo4jProjectionGraphStoreNormalizationSupport.NormalizeConstraintName(
                $"projection_graph_node_scope_id_{_nodeLabel}");
            var cypher = Neo4jProjectionGraphStoreCypherSupport.BuildCreateNodeConstraintCypher(
                _nodeLabel,
                nodeConstraintName);
            await ExecuteWriteAsync(cypher, new Dictionary<string, object?>(), ct);
            _schemaInitialized = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task ExecuteWriteAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Write);
        var cursor = await session.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        ct.ThrowIfCancellationRequested();
    }

    private async Task ExecuteWriteTransactionAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Write);
        await using var transaction = await session.BeginTransactionAsync();
        var cursor = await transaction.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
        await transaction.CommitAsync();
        ct.ThrowIfCancellationRequested();
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
}
