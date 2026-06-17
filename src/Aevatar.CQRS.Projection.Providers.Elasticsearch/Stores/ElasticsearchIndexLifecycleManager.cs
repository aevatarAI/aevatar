using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

/// <summary>
/// Owns the physical-index lifecycle behind a stable read/write alias name.
///
/// Reconciliation state machine, applied once per <see cref="EnsureIndexAsync"/>
/// invocation per alias name in a process:
///
/// 1. Alias exists and points at <c>{alias}-v{fingerprint}</c> matching the
///    augmented metadata fingerprint → no-op.
/// 2. Alias exists and has exactly one backing physical, but the physical
///    suffix differs from the current fingerprint. Create the expected
///    physical, reindex from the old physical, then atomically remove the
///    old alias target and add the expected target.
/// 3. No alias exists but a bare index with the alias name does - this is a
///    pre-aliased prod index from before the lifecycle landed. Create the
///    new physical with expected mapping, reindex from the bare index, then
///    run an atomic <c>_aliases</c> call that adds the alias to the new
///    physical and removes the bare index (<c>remove_index</c> action).
///    One ES call, single-shot bare-to-aliased migration.
/// 4. Nothing exists: greenfield. Create the new physical with the alias
///    wired in via the index <c>aliases</c> payload key.
///
/// Reads and writes through the store stay on the alias name. ES routes
/// alias reads through to the underlying physical automatically; alias
/// writes work when the alias has a single backing index (the steady state
/// outside of the short reindex+swap window). Concurrent host startups
/// within a process are guarded by <see cref="_initLock"/>; cross-pod
/// safety relies on ES idempotency (create-index conflict on an
/// already-existing physical is treated as success, alias swap is atomic).
/// </summary>
internal sealed class ElasticsearchIndexLifecycleManager : IDisposable
{
    private static readonly TimeSpan ReindexCompletionBudget = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _initializedAliases = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly bool _autoCreate;
    private readonly ILogger _logger;

    public ElasticsearchIndexLifecycleManager(HttpClient httpClient, bool autoCreate, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _autoCreate = autoCreate;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task EnsureIndexAsync(
        string aliasName,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        if (!_autoCreate)
            return;

        lock (_stateGate)
        {
            if (_initializedAliases.Contains(aliasName))
                return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            lock (_stateGate)
            {
                if (_initializedAliases.Contains(aliasName))
                    return;
            }

            await ReconcileAsync(aliasName, metadata, ct);
            MarkInitialized(aliasName);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ProjectionIndexConsistencyResult> CheckConsistencyAsync(
        string aliasName,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        var fingerprint = ElasticsearchProjectionSchemaFingerprint.Compute(metadata);
        var expectedPhysical = $"{aliasName}-v{fingerprint}";

        var aliasResolution = await ResolveAliasAsync(aliasName, ct);
        if (aliasResolution.Targets.Count == 1)
        {
            var currentAliasTarget = aliasResolution.Targets[0];
            if (string.Equals(currentAliasTarget, expectedPhysical, StringComparison.Ordinal))
            {
                return new ProjectionIndexConsistencyResult(
                    "Elasticsearch",
                    aliasName,
                    expectedPhysical,
                    currentAliasTarget,
                    ProjectionIndexConsistencyStatus.Consistent,
                    "Projection index alias points to the expected physical index.");
            }

            return new ProjectionIndexConsistencyResult(
                "Elasticsearch",
                aliasName,
                expectedPhysical,
                currentAliasTarget,
                ProjectionIndexConsistencyStatus.Drifted,
                "Projection index schema drift detected: alias points to a physical index with a different schema fingerprint.");
        }

        if (aliasResolution.Targets.Count > 1)
        {
            return new ProjectionIndexConsistencyResult(
                "Elasticsearch",
                aliasName,
                expectedPhysical,
                string.Join(",", aliasResolution.Targets),
                ProjectionIndexConsistencyStatus.Drifted,
                "Projection index schema drift detected: alias points to multiple physical indices.");
        }

        var bareExists = await IndexExistsAsync(aliasName, ct);
        if (bareExists)
        {
            return new ProjectionIndexConsistencyResult(
                "Elasticsearch",
                aliasName,
                expectedPhysical,
                aliasName,
                ProjectionIndexConsistencyStatus.Drifted,
                "Projection index schema drift detected: index exists as a bare index instead of an aliased physical index.");
        }

        return new ProjectionIndexConsistencyResult(
            "Elasticsearch",
            aliasName,
            expectedPhysical,
            null,
            ProjectionIndexConsistencyStatus.Missing,
            "Projection index alias is not present.");
    }

    private async Task ReconcileAsync(string aliasName, DocumentIndexMetadata metadata, CancellationToken ct)
    {
        var fingerprint = ElasticsearchProjectionSchemaFingerprint.Compute(metadata);
        var expectedPhysical = $"{aliasName}-v{fingerprint}";

        var aliasResolution = await ResolveAliasAsync(aliasName, ct);
        if (aliasResolution.Targets.Count == 1)
        {
            var currentAliasTarget = aliasResolution.Targets[0];
            if (string.Equals(currentAliasTarget, expectedPhysical, StringComparison.Ordinal))
                return;

            await MigrateAliasFingerprintAsync(aliasName, currentAliasTarget, expectedPhysical, metadata, ct);
            return;
        }

        if (aliasResolution.Targets.Count > 1)
        {
            throw new ProjectionIndexSchemaDriftException(
                "Elasticsearch",
                aliasName,
                string.Join(",", aliasResolution.Targets),
                expectedPhysical,
                "Elasticsearch projection index schema drift detected: alias points to multiple physical indices.");
        }

        var bareExists = await IndexExistsAsync(aliasName, ct);
        if (bareExists)
        {
            await WrapBareIndexAsync(aliasName, expectedPhysical, metadata, ct);
            return;
        }

        await CreateFreshAliasedAsync(aliasName, expectedPhysical, metadata, ct);
    }

    private async Task<AliasResolution> ResolveAliasAsync(string aliasName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"_alias/{Uri.EscapeDataString(aliasName)}");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return AliasResolution.Missing;

        await EnsureSuccessAsync(response, $"resolve alias '{aliasName}'", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // Expected response shape:
        //   { "<physical>": { "aliases": { "<aliasName>": { ... } } } }
        // For our convention exactly one physical backs the alias. Any
        // other shape (boolean, array, missing 'aliases' key, ack-only) is
        // treated as "no alias resolved" so the caller falls through to
        // the bare-index / greenfield branches instead of throwing on
        // unexpected payloads.
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return AliasResolution.Missing;

        var targets = new List<string>();
        foreach (var physical in doc.RootElement.EnumerateObject())
        {
            if (physical.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (!physical.Value.TryGetProperty("aliases", out var aliases))
                continue;
            if (aliases.ValueKind != JsonValueKind.Object)
                continue;
            if (aliases.TryGetProperty(aliasName, out _))
                targets.Add(physical.Name);
        }

        return targets.Count == 0
            ? AliasResolution.Missing
            : new AliasResolution(targets);
    }

    private async Task<bool> IndexExistsAsync(string indexOrAliasName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Uri.EscapeDataString(indexOrAliasName));
        using var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task CreateFreshAliasedAsync(
        string aliasName,
        string physical,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        var withAlias = metadata with
        {
            Aliases = new Dictionary<string, object?>(metadata.Aliases, StringComparer.Ordinal)
            {
                [aliasName] = new Dictionary<string, object?>(StringComparer.Ordinal),
            },
        };

        await CreatePhysicalAsync(physical, withAlias, ct);
        _logger.LogInformation(
            "Projection index lifecycle: created fresh aliased physical alias={Alias} physical={Physical}",
            aliasName, physical);
    }

    private async Task WrapBareIndexAsync(
        string aliasName,
        string newPhysical,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        await CreatePhysicalAsync(newPhysical, metadata, ct);
        await ReindexAsync(sourceIndex: aliasName, destIndex: newPhysical, ct);
        await ExecuteAliasActionsAsync(
            new object[]
            {
                new Dictionary<string, object?> { ["add"] = new Dictionary<string, object?> { ["index"] = newPhysical, ["alias"] = aliasName } },
                new Dictionary<string, object?> { ["remove_index"] = new Dictionary<string, object?> { ["index"] = aliasName } },
            },
            description: $"wrap bare index '{aliasName}' into aliased physical '{newPhysical}'",
            ct);
        _logger.LogInformation(
            "Projection index lifecycle: wrapped bare index into aliased physical alias={Alias} physical={Physical}",
            aliasName, newPhysical);
    }

    private async Task MigrateAliasFingerprintAsync(
        string aliasName,
        string oldPhysical,
        string newPhysical,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        await CreatePhysicalAsync(newPhysical, metadata, ct);
        await ReindexAsync(sourceIndex: oldPhysical, destIndex: newPhysical, ct);
        await ExecuteAliasActionsAsync(
            new object[]
            {
                new Dictionary<string, object?> { ["remove"] = new Dictionary<string, object?> { ["index"] = oldPhysical, ["alias"] = aliasName } },
                new Dictionary<string, object?> { ["add"] = new Dictionary<string, object?> { ["index"] = newPhysical, ["alias"] = aliasName } },
            },
            description: $"migrate alias '{aliasName}' from physical '{oldPhysical}' to '{newPhysical}'",
            ct);
        _logger.LogInformation(
            "Projection index lifecycle: migrated alias fingerprint alias={Alias} oldPhysical={OldPhysical} newPhysical={NewPhysical}",
            aliasName, oldPhysical, newPhysical);
    }

    private async Task CreatePhysicalAsync(string physical, DocumentIndexMetadata metadata, CancellationToken ct)
    {
        var payload = ElasticsearchProjectionDocumentStorePayloadSupport.BuildIndexInitializationPayload(metadata);
        using var request = new HttpRequestMessage(HttpMethod.Put, Uri.EscapeDataString(physical))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.BadRequest &&
            body.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Elasticsearch physical index creation failed for '{physical}': {(int)response.StatusCode} {response.ReasonPhrase}. body={body}");
    }

    private async Task ReindexAsync(string sourceIndex, string destIndex, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = new Dictionary<string, object?> { ["index"] = sourceIndex },
            ["dest"] = new Dictionary<string, object?> { ["index"] = destIndex, ["op_type"] = "create" },
        });

        var timeoutSeconds = (int)Math.Max(1, ReindexCompletionBudget.TotalSeconds);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"_reindex?wait_for_completion=true&timeout={timeoutSeconds}s&refresh=true")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"reindex '{sourceIndex}' to '{destIndex}'", ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("failures", out var failures) &&
            failures.ValueKind == JsonValueKind.Array &&
            failures.GetArrayLength() > 0)
        {
            throw new InvalidOperationException(
                $"Elasticsearch reindex from '{sourceIndex}' to '{destIndex}' reported per-document failures: {body}");
        }
        if (doc.RootElement.TryGetProperty("timed_out", out var timedOut) &&
            timedOut.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                $"Elasticsearch reindex from '{sourceIndex}' to '{destIndex}' timed out within {ReindexCompletionBudget}.");
        }
    }

    private async Task ExecuteAliasActionsAsync(IReadOnlyList<object> actions, string description, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["actions"] = actions,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "_aliases")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, description, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Elasticsearch projection index lifecycle operation '{operation}' failed: {(int)response.StatusCode} {response.ReasonPhrase}. body={body}");
    }

    private void MarkInitialized(string aliasName)
    {
        lock (_stateGate)
            _initializedAliases.Add(aliasName);
    }

    public void Dispose() => _initLock.Dispose();

    private sealed record AliasResolution(IReadOnlyList<string> Targets)
    {
        public static AliasResolution Missing { get; } = new(Array.Empty<string>());
    }
}
