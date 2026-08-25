using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

/// <summary>
/// Owns the physical-index lifecycle behind a stable read/write alias name.
///
/// Index reconciliation state machine, applied once per alias name in a process across
/// <see cref="EnsureIndexAsync"/> and explicit startup reconciliation:
///
/// 1. Alias exists and points at <c>{alias}-v{fingerprint}</c> matching the
///    augmented metadata fingerprint → no-op.
/// 2. Alias exists but points at a different physical → fail loud. The
///    alias+fingerprint lifecycle is the only schema-drift authority, so
///    projection must refuse to write instead of repairing drift through a
///    query-time or projection-turn mapping reader.
/// 3. No alias exists but a bare index with the alias name does - this is a
///    pre-aliased prod index from before the lifecycle landed. Create the
///    new physical with expected mapping, reindex from the bare index, then
///    run an atomic <c>_aliases</c> call that adds the alias to the new
///    physical and removes the bare index (<c>remove_index</c> action).
///    One ES call, single-shot bare → aliased migration.
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
public sealed class ElasticsearchIndexLifecycleManager : IDisposable
{
    private static readonly TimeSpan ReindexCompletionBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A synchronous <c>_reindex</c> is bounded by <see cref="ReindexCompletionBudget"/>; the
    /// HTTP client that carries it must therefore outlive that budget instead of the store's
    /// short per-request timeout, otherwise the client cancels a still-running server-side
    /// copy and the alias is left drifted while the destination fills in the background.
    /// </summary>
    public static readonly TimeSpan ReindexRequestTimeout = ReindexCompletionBudget + TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _initializedAliases = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly HttpClient _reindexHttpClient;
    private readonly bool _autoCreate;
    private readonly ILogger _logger;

    public ElasticsearchIndexLifecycleManager(HttpClient httpClient, bool autoCreate, ILogger? logger = null)
        : this(httpClient, autoCreate, logger, reindexHttpClient: null)
    {
    }

    /// <param name="reindexHttpClient">
    /// Optional client dedicated to long-running <c>_reindex</c> calls; it must share the base
    /// address and credentials of <paramref name="httpClient"/> and allow at least
    /// <see cref="ReindexRequestTimeout"/>. When omitted, <paramref name="httpClient"/> is used.
    /// </param>
    public ElasticsearchIndexLifecycleManager(
        HttpClient httpClient,
        bool autoCreate,
        ILogger? logger,
        HttpClient? reindexHttpClient)
    {
        _httpClient = httpClient;
        _reindexHttpClient = reindexHttpClient ?? httpClient;
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

            // Refactor (iter98/cluster-743): Old pattern: fingerprint drift triggered
            // an in-place lifecycle migration and reindex. New principle: alias +
            // fingerprint is the sole schema-drift truth source; a mismatched
            // physical means configuration drift and projection refuses to proceed.
            throw new ProjectionIndexSchemaDriftException(
                "Elasticsearch",
                aliasName,
                currentAliasTarget,
                expectedPhysical);
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

    /// <summary>
    /// Data-safe schema-drift self-heal, intended to run once at host startup (NOT on the
    /// write path, which keeps the fail-loud <see cref="ReconcileAsync"/> contract). Identical
    /// to <see cref="ReconcileAsync"/> EXCEPT the drift case: instead of throwing, it reindexes
    /// the existing docs forward from the current physical into the expected fingerprinted
    /// physical and atomically repoints the alias. The old physical is retained (alias
    /// <c>remove</c>, not <c>remove_index</c>) as a rollback artifact — cleanup is a separate
    /// operational GC. Reindex uses <c>op_type=create</c> and hard-fails on per-doc failures or
    /// timeout (see <see cref="ReindexAsync"/>), so the alias is never swapped onto a
    /// partially-copied physical. It NEVER falls back to an empty create on a populated alias:
    /// the projector has no event-log replay, so an empty repoint would be silent data loss.
    /// </summary>
    public async Task ReconcileWithReindexAsync(string aliasName, DocumentIndexMetadata metadata, CancellationToken ct)
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

            await ReconcileWithReindexCoreAsync(aliasName, metadata, ct);
            MarkInitialized(aliasName);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task ReconcileWithReindexCoreAsync(
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
                return;

            // Drift: the alias points to an older physical. Copy data forward, then atomic swap.
            // If the expected physical already exists, a prior pod/deploy created it but may not
            // have finished filling it (an interrupted copy leaves a partial destination while
            // Elasticsearch keeps copying in the background), so top it up from the source
            // before repointing: nothing writes to the destination until the alias moves, and
            // nothing writes to the drifted source because writers fail closed on drift, so an
            // overwrite copy is exactly source == destination.
            var expectedExists = await IndexExistsAsync(expectedPhysical, ct);
            if (!expectedExists)
            {
                await CreatePhysicalAsync(expectedPhysical, metadata, ct);
                await ReindexAsync(sourceIndex: currentAliasTarget, destIndex: expectedPhysical, ct);
            }
            else
            {
                await ReindexAsync(
                    sourceIndex: currentAliasTarget,
                    destIndex: expectedPhysical,
                    ct,
                    ReindexMode.OverwriteDestination);
            }

            await ExecuteAliasActionsAsync(
                new object[]
                {
                    new Dictionary<string, object?> { ["add"] = new Dictionary<string, object?> { ["index"] = expectedPhysical, ["alias"] = aliasName } },
                    RemoveAliasAction(currentAliasTarget, aliasName),
                },
                description: $"reindex-heal schema drift: repoint alias '{aliasName}' {currentAliasTarget} → {expectedPhysical}",
                ct);

            _logger.LogInformation(
                "Projection index lifecycle: reindex-healed schema drift alias={Alias} old={OldPhysical} new={NewPhysical} reindexMode={ReindexMode}",
                aliasName, currentAliasTarget, expectedPhysical, expectedExists ? "top-up" : "initial");
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

    /// <summary>
    /// Provisions a non-readmodel artifact behind a versioned alias even when request-path
    /// auto-create is disabled. A pre-alias legacy index is copied into the new physical and
    /// retained unchanged; only the new alias is added after a complete reindex.
    /// </summary>
    public async Task ReconcileArtifactWithReindexAsync(
        string aliasName,
        string legacyIndexName,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyIndexName);
        ArgumentNullException.ThrowIfNull(metadata);

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

            try
            {
                await ReconcileArtifactCoreAsync(aliasName, legacyIndexName, metadata, ct);
                MarkInitialized(aliasName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Elasticsearch artifact index reconcile failed. alias={Alias} errorType={ErrorType}",
                    aliasName,
                    exception.GetType().Name);
                throw new InvalidOperationException(
                    $"Elasticsearch artifact index reconcile failed for alias '{aliasName}'. " +
                    $"errorType={exception.GetType().Name}");
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task ReconcileArtifactCoreAsync(
        string aliasName,
        string legacyIndexName,
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
                return;

            var expectedExists = await IndexExistsAsync(expectedPhysical, ct);
            if (!expectedExists)
                await CreatePhysicalAsync(expectedPhysical, metadata, ct);
            await ReindexAsync(currentAliasTarget, expectedPhysical, ct, allowExistingDocuments: true);

            await ExecuteAliasActionsAsync(
                new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["add"] = new Dictionary<string, object?>
                        {
                            ["index"] = expectedPhysical,
                            ["alias"] = aliasName,
                        },
                    },
                    RemoveAliasAction(currentAliasTarget, aliasName),
                },
                $"reindex artifact schema drift for alias '{aliasName}'",
                ct);

            _logger.LogInformation(
                "Elasticsearch artifact index reconcile completed. alias={Alias} result={Result}",
                aliasName,
                "schema_reindexed");
            return;
        }

        if (aliasResolution.Targets.Count > 1)
        {
            throw new ProjectionIndexSchemaDriftException(
                "Elasticsearch",
                aliasName,
                string.Join(",", aliasResolution.Targets),
                expectedPhysical,
                "Elasticsearch artifact index schema drift detected: alias points to multiple physical indices.");
        }

        if (await IndexExistsAsync(aliasName, ct))
        {
            throw new InvalidOperationException(
                $"Elasticsearch artifact alias '{aliasName}' is blocked by a bare index. " +
                "Automatic reconciliation will not delete or replace it.");
        }

        if (await IndexExistsAsync(legacyIndexName, ct))
        {
            await CreatePhysicalAsync(expectedPhysical, metadata, ct);
            await ReindexAsync(legacyIndexName, expectedPhysical, ct, allowExistingDocuments: true);
            await ExecuteAliasActionsAsync(
                new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["add"] = new Dictionary<string, object?>
                        {
                            ["index"] = expectedPhysical,
                            ["alias"] = aliasName,
                        },
                    },
                },
                $"attach artifact alias '{aliasName}' after legacy reindex",
                ct);

            _logger.LogInformation(
                "Elasticsearch artifact index reconcile completed. alias={Alias} result={Result}",
                aliasName,
                "legacy_reindexed");
            return;
        }

        await CreateFreshAliasedAsync(aliasName, expectedPhysical, metadata, ct);
    }

    private static Dictionary<string, object?> RemoveAliasAction(string physical, string alias) =>
        new() { ["remove"] = new Dictionary<string, object?> { ["index"] = physical, ["alias"] = alias } };

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

    private enum ReindexMode
    {
        /// <summary>Every source document must be created in an empty destination.</summary>
        CreateAll = 0,

        /// <summary>Documents already present in the destination are kept; the rest are created.</summary>
        KeepExistingDocuments = 1,

        /// <summary>The destination is made equal to the source, overwriting stale copies.</summary>
        OverwriteDestination = 2,
    }

    private Task ReindexAsync(
        string sourceIndex,
        string destIndex,
        CancellationToken ct,
        bool allowExistingDocuments = false) =>
        ReindexAsync(
            sourceIndex,
            destIndex,
            ct,
            allowExistingDocuments ? ReindexMode.KeepExistingDocuments : ReindexMode.CreateAll);

    private async Task ReindexAsync(
        string sourceIndex,
        string destIndex,
        CancellationToken ct,
        ReindexMode mode)
    {
        var allowExistingDocuments = mode == ReindexMode.KeepExistingDocuments;
        var destination = new Dictionary<string, object?> { ["index"] = destIndex };
        if (mode != ReindexMode.OverwriteDestination)
            destination["op_type"] = "create";
        var payloadProperties = new Dictionary<string, object?>
        {
            ["source"] = new Dictionary<string, object?> { ["index"] = sourceIndex },
            ["dest"] = destination,
        };
        if (allowExistingDocuments)
            payloadProperties["conflicts"] = "proceed";
        var payload = JsonSerializer.Serialize(payloadProperties);

        var timeoutSeconds = (int)Math.Max(1, ReindexCompletionBudget.TotalSeconds);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"_reindex?wait_for_completion=true&timeout={timeoutSeconds}s&refresh=true")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await _reindexHttpClient.SendAsync(request, ct);
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

        if (allowExistingDocuments &&
            doc.RootElement.TryGetProperty("total", out var totalNode) && totalNode.TryGetInt64(out var total) &&
            doc.RootElement.TryGetProperty("created", out var createdNode) && createdNode.TryGetInt64(out var created) &&
            doc.RootElement.TryGetProperty("version_conflicts", out var conflictsNode) &&
            conflictsNode.TryGetInt64(out var versionConflicts) &&
            created + versionConflicts != total)
        {
            throw new InvalidOperationException(
                $"Elasticsearch reindex from '{sourceIndex}' to '{destIndex}' did not account for every source document.");
        }

        if (mode == ReindexMode.OverwriteDestination &&
            doc.RootElement.TryGetProperty("total", out var overwriteTotalNode) &&
            overwriteTotalNode.TryGetInt64(out var overwriteTotal) &&
            doc.RootElement.TryGetProperty("created", out var overwriteCreatedNode) &&
            overwriteCreatedNode.TryGetInt64(out var overwriteCreated) &&
            doc.RootElement.TryGetProperty("updated", out var overwriteUpdatedNode) &&
            overwriteUpdatedNode.TryGetInt64(out var overwriteUpdated) &&
            overwriteCreated + overwriteUpdated != overwriteTotal)
        {
            throw new InvalidOperationException(
                $"Elasticsearch top-up reindex from '{sourceIndex}' to '{destIndex}' did not account for every source document.");
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
