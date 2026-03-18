using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed partial class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
    : IProjectionDocumentReader<TReadModel, TKey>,
      IProjectionDocumentWriter<TReadModel>,
      IDisposable
    where TReadModel : class, IProjectionReadModel
{
    private const string ProviderName = "Elasticsearch";
    private const int MaxOptimisticWriteAttempts = 3;

    private readonly HttpClient _httpClient;
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly string _indexPrefix;
    private readonly string _indexName;
    private readonly int _queryTakeMax;
    private readonly bool _autoCreateIndex;
    private readonly string _defaultSortField;
    private readonly ElasticsearchMissingIndexBehavior _missingIndexBehavior;
    private readonly bool _supportsDynamicIndexing;
    private readonly DocumentIndexMetadata _indexMetadata;
    private readonly Func<TReadModel, string?>? _indexScopeSelector;
    private readonly ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>> _logger;
    private readonly SemaphoreSlim _indexInitializationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly Lock _dynamicIndexStateGate = new();
    private readonly HashSet<string> _initializedIndices = new(StringComparer.Ordinal);

    public ElasticsearchProjectionDocumentStore(
        ElasticsearchProjectionDocumentStoreOptions options,
        DocumentIndexMetadata indexMetadata,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, string?>? indexScopeSelector = null,
        ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>? logger = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySelector);

        var endpoint = ElasticsearchProjectionDocumentStoreNamingSupport.ResolvePrimaryEndpoint(options.Endpoints);
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.BaseAddress = endpoint;
        _httpClient.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, options.RequestTimeoutMs));

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = $"{options.Username}:{options.Password}";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        var normalizedMetadata = ElasticsearchProjectionDocumentStoreMetadataSupport.NormalizeMetadata(indexMetadata);
        _indexPrefix = options.IndexPrefix?.Trim() ?? "";
        var normalizedScope = ElasticsearchProjectionDocumentStoreNamingSupport.NormalizeToken(normalizedMetadata.IndexName);
        if (normalizedScope.Length == 0)
            normalizedScope = "readmodel";
        _indexName = ElasticsearchProjectionDocumentStoreNamingSupport.BuildIndexName(_indexPrefix, normalizedScope);
        _queryTakeMax = options.QueryTakeMax > 0 ? options.QueryTakeMax : 200;
        _autoCreateIndex = options.AutoCreateIndex;
        _missingIndexBehavior = options.MissingIndexBehavior;
        _supportsDynamicIndexing = indexScopeSelector is not null;
        _indexMetadata = normalizedMetadata with { IndexName = _indexName };
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _indexScopeSelector = indexScopeSelector;
        _defaultSortField = options.DefaultSortField?.Trim() ?? "";
        _logger = logger ?? NullLogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>.Instance;
    }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default) =>
        UpsertCoreAsync(readModel, allowCreateIndex: true, ct);

    public async Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("get");
        await EnsureIndexAsync(_indexName, _indexMetadata, ct);

        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            return null;

        using var response = await _httpClient.GetAsync($"{_indexName}/_doc/{Uri.EscapeDataString(keyValue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (TryHandleMissingIndexForRead("get", payload))
                return null;
            return null;
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return null;

        return DeserializeOrNull(sourceNode.GetRawText());
    }

    public async Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("query");
        await EnsureIndexAsync(_indexName, _indexMetadata, ct);
        var boundedTake = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, _queryTakeMax);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_indexName}/_search")
        {
                Content = new StringContent(
                    ElasticsearchProjectionDocumentStorePayloadSupport.BuildQueryPayloadJson(
                        query,
                        _defaultSortField,
                        boundedTake),
                    Encoding.UTF8,
                    "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (TryHandleMissingIndexForRead("query", payload))
                return ProjectionDocumentQueryResult<TReadModel>.Empty;
            return ProjectionDocumentQueryResult<TReadModel>.Empty;
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "query", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        if (!jsonDoc.RootElement.TryGetProperty("hits", out var hitsNode) ||
            !hitsNode.TryGetProperty("hits", out var hitItems))
        {
            return ProjectionDocumentQueryResult<TReadModel>.Empty;
        }

        var items = new List<TReadModel>();
        string? nextCursor = null;
        foreach (var hit in hitItems.EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var sourceNode))
                continue;

            var item = DeserializeOrNull(sourceNode.GetRawText());
            if (item != null)
                items.Add(item);

            nextCursor = ElasticsearchProjectionDocumentStorePayloadSupport.BuildNextCursor(hit);
        }

        long? totalCount = null;
        if (query.IncludeTotalCount &&
            ElasticsearchProjectionDocumentStorePayloadSupport.TryReadTotalCount(jsonDoc.RootElement, out var total))
        {
            totalCount = total;
        }

        return new ProjectionDocumentQueryResult<TReadModel>
        {
            Items = items,
            NextCursor = items.Count == boundedTake ? nextCursor : null,
            TotalCount = totalCount,
        };
    }

    private async Task<ProjectionWriteResult> UpsertCoreAsync(
        TReadModel readModel,
        bool allowCreateIndex,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        var indexTarget = ResolveIndexTarget(readModel);
        if (allowCreateIndex)
            await EnsureIndexAsync(indexTarget.IndexName, indexTarget.Metadata, ct);

        var keyValue = ResolveReadModelKey(readModel);
        var payload = SerializePayload(readModel, keyValue);
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            for (var attempt = 1; attempt <= MaxOptimisticWriteAttempts; attempt++)
            {
                var existing = await TryGetExistingStateAsync(indexTarget.IndexName, keyValue, ct);
                var result = ProjectionWriteResultEvaluator.Evaluate(existing.ReadModel, readModel);
                if (!result.IsApplied)
                {
                    var skippedElapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                    _logger.LogInformation(
                        "Projection read-model write skipped. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                        ProviderName,
                        typeof(TReadModel).FullName,
                        keyValue,
                        skippedElapsedMs,
                        result.Disposition);
                    return result;
                }

                using var request = BuildConditionalUpsertRequest(indexTarget.IndexName, keyValue, payload, existing);
                using var response = await _httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                    _logger.LogInformation(
                        "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                        ProviderName,
                        typeof(TReadModel).FullName,
                        keyValue,
                        elapsedMs,
                        ProjectionWriteDisposition.Applied);
                    return ProjectionWriteResult.Applied();
                }

                if (response.StatusCode != HttpStatusCode.Conflict)
                    await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "upsert", ct);

                _logger.LogInformation(
                    "Projection read-model write hit optimistic concurrency conflict and will re-evaluate. provider={Provider} readModelType={ReadModelType} key={Key} attempt={Attempt}/{MaxAttempts}",
                    ProviderName,
                    typeof(TReadModel).FullName,
                    keyValue,
                    attempt,
                    MaxOptimisticWriteAttempts);
            }

            var reconciled = await TryGetExistingStateAsync(indexTarget.IndexName, keyValue, ct);
            var reconciledResult = ProjectionWriteResultEvaluator.Evaluate(reconciled.ReadModel, readModel);
            if (!reconciledResult.IsApplied)
            {
                var skippedElapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
                _logger.LogInformation(
                    "Projection read-model write reconciled after optimistic concurrency conflict. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                    ProviderName,
                    typeof(TReadModel).FullName,
                    keyValue,
                    skippedElapsedMs,
                    reconciledResult.Disposition);
                return reconciledResult;
            }

            throw new InvalidOperationException(
                $"Elasticsearch optimistic concurrency write could not be reconciled for read-model '{typeof(TReadModel).FullName}' key '{keyValue}'.");
        }
        catch (Exception ex)
        {
            LogWriteFailure(keyValue, startedAt, ex);
            throw;
        }
    }

    private async Task<ExistingReadModelState> TryGetExistingStateAsync(
        string indexName,
        string keyValue,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync($"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(payload))
            {
                if (_autoCreateIndex || _missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
                    throw BuildMissingIndexException("get", payload);

                return ExistingReadModelState.Missing;
            }

            return ExistingReadModelState.Missing;
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        var seqNo = TryReadLong(jsonDoc.RootElement, "_seq_no");
        var primaryTerm = TryReadLong(jsonDoc.RootElement, "_primary_term");
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return new ExistingReadModelState(null, seqNo, primaryTerm);

        return new ExistingReadModelState(DeserializeOrNull(sourceNode.GetRawText()), seqNo, primaryTerm);
    }

    private bool TryHandleMissingIndexForRead(string operation, string payload)
    {
        if (!ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(payload))
            return false;

        if (_autoCreateIndex || _missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
            throw BuildMissingIndexException(operation, payload);

        _logger.LogWarning(
            "Projection read-model index is missing. provider={Provider} readModelType={ReadModelType} index={Index} operation={Operation} behavior={Behavior}",
            ProviderName,
            typeof(TReadModel).FullName,
            _indexName,
            operation,
            _missingIndexBehavior);
        return true;
    }

    private InvalidOperationException BuildMissingIndexException(string operation, string payload)
    {
        return new InvalidOperationException(
            $"Elasticsearch index '{_indexName}' was not found during '{operation}' for read-model '{typeof(TReadModel).FullName}'. " +
            $"Configure index bootstrap or set '{nameof(ElasticsearchProjectionDocumentStoreOptions.AutoCreateIndex)}=true'. " +
            $"body={ElasticsearchProjectionDocumentStoreNamingSupport.TruncatePayload(payload)}");
    }

    private void LogWriteFailure(
        string keyValue,
        DateTimeOffset startedAt,
        Exception ex)
    {
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogError(
            ex,
            "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
            ProviderName,
            typeof(TReadModel).FullName,
            keyValue,
            elapsedMs,
            "failed",
            ex.GetType().Name);
    }

    private string ResolveReadModelKey(TReadModel readModel)
    {
        var key = _keySelector(readModel);
        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for Elasticsearch persistence.");
        return keyValue;
    }

    private static HttpRequestMessage BuildConditionalUpsertRequest(
        string indexName,
        string keyValue,
        string payload,
        ExistingReadModelState existing)
    {
        var requestPath = existing.ReadModel == null
            ? $"{indexName}/_create/{Uri.EscapeDataString(keyValue)}"
            : $"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}?if_seq_no={existing.SeqNo}&if_primary_term={existing.PrimaryTerm}";
        return new HttpRequestMessage(HttpMethod.Put, requestPath)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private static long TryReadLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
            return -1;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => parsed,
            _ => -1,
        };
    }

    private string FormatKey(TKey key)
    {
        var keyValue = _keyFormatter(key)?.Trim() ?? "";
        return keyValue;
    }

    private string SerializePayload(TReadModel readModel, string keyValue)
    {
        var payload = JsonSerializer.SerializeToNode(readModel, _jsonOptions) as JsonObject;
        if (payload == null)
        {
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' could not be serialized into a JSON object for Elasticsearch persistence.");
        }

        payload[ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField] = keyValue;
        return payload.ToJsonString(_jsonOptions);
    }

    private TReadModel? DeserializeOrNull(string json)
    {
        var value = JsonSerializer.Deserialize<TReadModel>(json, _jsonOptions);
        if (value == null)
            return null;

        // Defensive copy to isolate caller-side mutation from cache/shared references.
        var copyPayload = JsonSerializer.Serialize(value, _jsonOptions);
        return JsonSerializer.Deserialize<TReadModel>(copyPayload, _jsonOptions);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _indexInitializationLock.Dispose();
    }

    private ResolvedIndexTarget ResolveIndexTarget(TReadModel readModel)
    {
        if (_indexScopeSelector is null)
            return new ResolvedIndexTarget(_indexName, _indexMetadata);

        var rawScope = _indexScopeSelector(readModel)?.Trim() ?? string.Empty;
        var normalizedScope = ElasticsearchProjectionDocumentStoreNamingSupport.NormalizeToken(
            rawScope.Length > 0
                ? rawScope
                : _indexMetadata.IndexName);
        if (normalizedScope.Length == 0)
            normalizedScope = "readmodel";

        var indexName = ElasticsearchProjectionDocumentStoreNamingSupport.BuildIndexName(_indexPrefix, normalizedScope);
        return new ResolvedIndexTarget(indexName, _indexMetadata with { IndexName = indexName });
    }

    private void ThrowIfDynamicReadModelQueriesUnsupported(string operation)
    {
        if (!_supportsDynamicIndexing)
            return;

        throw new InvalidOperationException(
            $"Elasticsearch '{operation}' is not supported for dynamically indexed read model '{typeof(TReadModel).FullName}'. " +
            "Use direct provider-native inspection/query capability for this read model type.");
    }

    private sealed record ResolvedIndexTarget(string IndexName, DocumentIndexMetadata Metadata);

    private sealed record ExistingReadModelState(
        TReadModel? ReadModel,
        long SeqNo,
        long PrimaryTerm)
    {
        public static ExistingReadModelState Missing { get; } = new(null, -1, -1);
    }
}
