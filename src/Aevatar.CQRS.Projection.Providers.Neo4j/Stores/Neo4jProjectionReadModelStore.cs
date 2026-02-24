using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.Stores;

public sealed class Neo4jProjectionReadModelStore<TReadModel, TKey>
    : IProjectionReadModelStore<TReadModel, TKey>,
      IProjectionStoreProviderMetadata,
      IAsyncDisposable
    where TReadModel : class
{
    private readonly IDriver _driver;
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly string _scope;
    private readonly string _database;
    private readonly int _listTakeMax;
    private readonly string _label;
    private readonly bool _autoCreateConstraints;
    private readonly ILogger<Neo4jProjectionReadModelStore<TReadModel, TKey>> _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private bool _schemaInitialized;

    public Neo4jProjectionReadModelStore(
        Neo4jProjectionReadModelStoreOptions options,
        string scope,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionReadModelProviderNames.Neo4j,
        ILogger<Neo4jProjectionReadModelStore<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(keySelector);

        _scope = scope.Trim();
        _database = options.Database?.Trim() ?? "";
        _listTakeMax = options.ListTakeMax > 0 ? options.ListTakeMax : 200;
        _label = NormalizeLabel(options.NodeLabel);
        _autoCreateConstraints = options.AutoCreateConstraints;
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _logger = logger ?? NullLogger<Neo4jProjectionReadModelStore<TReadModel, TKey>>.Instance;

        var auth = string.IsNullOrWhiteSpace(options.Username)
            ? AuthTokens.None
            : AuthTokens.Basic(options.Username.Trim(), options.Password ?? "");
        _driver = GraphDatabase.Driver(options.Uri, auth, config =>
            config.WithConnectionTimeout(TimeSpan.FromMilliseconds(Math.Max(1000, options.RequestTimeoutMs))));

        ProviderCapabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing: true,
            indexKinds: [ProjectionReadModelIndexKind.Graph],
            supportsAliases: false,
            supportsSchemaValidation: true,
            supportsRelations: true,
            supportsRelationTraversal: true);
    }

    public ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        var startedAt = DateTimeOffset.UtcNow;
        var key = "";
        try
        {
            await EnsureSchemaAsync(ct);

            key = ResolveReadModelKey(readModel);
            var payload = JsonSerializer.Serialize(readModel, _jsonOptions);
            var updatedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var cypher = $"MERGE (n:{_label} {{scope: $scope, id: $id}}) " +
                         "SET n.payload = $payload, n.updatedAtEpochMs = $updatedAtEpochMs";
            var parameters = new Dictionary<string, object?>
            {
                ["scope"] = _scope,
                ["id"] = key,
                ["payload"] = payload,
                ["updatedAtEpochMs"] = updatedAtEpochMs,
            };

            await ExecuteWriteAsync(cypher, parameters, ct);

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderCapabilities.ProviderName,
                typeof(TReadModel).FullName,
                key,
                elapsedMs,
                "ok");
        }
        catch (Exception ex)
        {
            LogWriteFailure(key, startedAt, ex);
            throw;
        }
    }

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        var keyValue = FormatKey(key);
        var startedAt = DateTimeOffset.UtcNow;
        var existing = await GetAsync(key, ct);
        if (existing == null)
        {
            var notFound = new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' with key '{keyValue}' was not found.");
            LogWriteFailure(keyValue, startedAt, notFound);
            throw notFound;
        }

        try
        {
            mutate(existing);
        }
        catch (Exception ex)
        {
            LogWriteFailure(keyValue, startedAt, ex);
            throw;
        }

        await UpsertAsync(existing, ct);
    }

    public async Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(ct);

        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            return null;

        var cypher = $"MATCH (n:{_label} {{scope: $scope, id: $id}}) " +
                     "RETURN n.payload AS payload LIMIT 1";
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = _scope,
            ["id"] = keyValue,
        };
        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        if (rows.Count == 0)
            return null;

        if (!rows[0].TryGetValue("payload", out var payloadValue))
            return null;

        var payload = payloadValue.As<string>();
        return Deserialize(payload);
    }

    public async Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(ct);
        var boundedTake = Math.Clamp(take, 1, _listTakeMax);
        var cypher = $"MATCH (n:{_label} {{scope: $scope}}) " +
                     "RETURN n.payload AS payload ORDER BY n.updatedAtEpochMs DESC LIMIT $take";
        var parameters = new Dictionary<string, object?>
        {
            ["scope"] = _scope,
            ["take"] = boundedTake,
        };

        var rows = await ExecuteReadAsync(cypher, parameters, ct);
        var readModels = new List<TReadModel>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("payload", out var payloadValue))
                continue;

            var item = Deserialize(payloadValue.As<string>());
            if (item != null)
                readModels.Add(item);
        }

        return readModels;
    }

    public async ValueTask DisposeAsync()
    {
        _schemaLock.Dispose();
        await _driver.DisposeAsync();
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

            var constraintName = NormalizeConstraintName($"projection_readmodel_scope_id_{_label}");
            var cypher = $"CREATE CONSTRAINT {constraintName} IF NOT EXISTS " +
                         $"FOR (n:{_label}) REQUIRE (n.scope, n.id) IS UNIQUE";
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

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ExecuteReadAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        await using var session = CreateSession(AccessMode.Read);
        var cursor = await session.RunAsync(cypher, parameters);
        var rows = await cursor.ToListAsync();
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

    private string ResolveReadModelKey(TReadModel readModel)
    {
        var key = _keySelector(readModel);
        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for Neo4j persistence.");
        return keyValue;
    }

    private string FormatKey(TKey key) => _keyFormatter(key)?.Trim() ?? "";

    private void LogWriteFailure(
        string key,
        DateTimeOffset startedAt,
        Exception ex)
    {
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogError(
            ex,
            "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
            ProviderCapabilities.ProviderName,
            typeof(TReadModel).FullName,
            key,
            elapsedMs,
            "failed",
            ex.GetType().Name);
    }

    private TReadModel? Deserialize(string payload)
    {
        var value = JsonSerializer.Deserialize<TReadModel>(payload, _jsonOptions);
        if (value == null)
            return null;

        var copied = JsonSerializer.Serialize(value, _jsonOptions);
        return JsonSerializer.Deserialize<TReadModel>(copied, _jsonOptions);
    }

    private static string NormalizeLabel(string rawLabel)
    {
        var label = (rawLabel ?? "").Trim();
        if (label.Length == 0)
            label = "ProjectionReadModel";

        var chars = label
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')
            .ToArray();
        return new string(chars);
    }

    private static string NormalizeConstraintName(string rawName)
    {
        var chars = rawName
            .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = new string(chars);
        if (normalized.Length == 0)
            return "projection_constraint";
        if (char.IsDigit(normalized[0]))
            normalized = $"c_{normalized}";
        return normalized;
    }
}
