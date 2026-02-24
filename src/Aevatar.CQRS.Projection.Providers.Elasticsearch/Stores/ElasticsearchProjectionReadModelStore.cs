using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed class ElasticsearchProjectionReadModelStore<TReadModel, TKey>
    : IDocumentProjectionStore<TReadModel, TKey>,
      IDisposable
    where TReadModel : class
{
    private const string ProviderName = "Elasticsearch";
    private const string DefaultListPrimarySortField = "CreatedAt";
    private const string DefaultListTiebreakSortField = "_id";

    private readonly HttpClient _httpClient;
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly string _indexName;
    private readonly int _listTakeMax;
    private readonly bool _autoCreateIndex;
    private readonly string _listSortField;
    private readonly ElasticsearchMissingIndexBehavior _missingIndexBehavior;
    private readonly int _mutateMaxRetryCount;
    private readonly DocumentIndexMetadata _indexMetadata;
    private readonly ILogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>> _logger;
    private readonly SemaphoreSlim _indexInitializationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private bool _indexInitialized;

    public ElasticsearchProjectionReadModelStore(
        ElasticsearchProjectionReadModelStoreOptions options,
        DocumentIndexMetadata indexMetadata,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        ILogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>>? logger = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySelector);

        var endpoint = ResolvePrimaryEndpoint(options.Endpoints);
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

        var normalizedMetadata = NormalizeMetadata(indexMetadata);
        var normalizedScope = NormalizeToken(normalizedMetadata.IndexName);
        if (normalizedScope.Length == 0)
            normalizedScope = "readmodel";
        _indexName = BuildIndexName(options.IndexPrefix, normalizedScope);
        _listTakeMax = options.ListTakeMax > 0 ? options.ListTakeMax : 200;
        _autoCreateIndex = options.AutoCreateIndex;
        _missingIndexBehavior = options.MissingIndexBehavior;
        _mutateMaxRetryCount = Math.Clamp(options.MutateMaxRetryCount, 0, 20);
        _indexMetadata = normalizedMetadata with { IndexName = _indexName };
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _listSortField = options.ListSortField?.Trim() ?? "";
        _logger = logger ?? NullLogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>>.Instance;
    }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default) =>
        UpsertCoreAsync(readModel, allowCreateIndex: true, ct);

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for Elasticsearch mutation.");

        var startedAt = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt <= _mutateMaxRetryCount; attempt++)
        {
            var snapshot = await GetDocumentSnapshotAsync(keyValue, ct);
            if (snapshot == null)
            {
                var notFound = new InvalidOperationException(
                    $"ReadModel '{typeof(TReadModel).FullName}' with key '{keyValue}' was not found.");
                LogWriteFailure(keyValue, startedAt, notFound);
                throw notFound;
            }

            try
            {
                mutate(snapshot.ReadModel);
            }
            catch (Exception ex)
            {
                LogWriteFailure(keyValue, startedAt, ex);
                throw;
            }

            try
            {
                await UpsertCoreAsync(
                    snapshot.ReadModel,
                    allowCreateIndex: true,
                    ct,
                    ifSeqNo: snapshot.SeqNo,
                    ifPrimaryTerm: snapshot.PrimaryTerm);
                return;
            }
            catch (ElasticsearchOptimisticConcurrencyException ex) when (attempt < _mutateMaxRetryCount)
            {
                _logger.LogWarning(
                    ex,
                "Projection read-model optimistic concurrency conflict. provider={Provider} readModelType={ReadModelType} key={Key} attempt={Attempt}/{MaxAttempts}",
                    ProviderName,
                    typeof(TReadModel).FullName,
                    keyValue,
                    attempt + 1,
                    _mutateMaxRetryCount + 1);
            }
            catch (ElasticsearchOptimisticConcurrencyException ex)
            {
                var conflict = new InvalidOperationException(
                    $"Elasticsearch optimistic concurrency update failed for read-model '{typeof(TReadModel).FullName}' with key '{keyValue}' after {_mutateMaxRetryCount + 1} attempt(s).",
                    ex);
                LogWriteFailure(keyValue, startedAt, conflict);
                throw conflict;
            }
        }
    }

    public async Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureIndexAsync(ct);

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

        await EnsureSuccessAsync(response, "get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return null;

        return DeserializeOrNull(sourceNode.GetRawText());
    }

    public async Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureIndexAsync(ct);
        var boundedTake = Math.Clamp(take, 1, _listTakeMax);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_indexName}/_search")
        {
            Content = new StringContent(BuildListPayloadJson(boundedTake), Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (TryHandleMissingIndexForRead("list", payload))
                return [];
            return [];
        }

        await EnsureSuccessAsync(response, "list", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        if (!jsonDoc.RootElement.TryGetProperty("hits", out var hitsNode) ||
            !hitsNode.TryGetProperty("hits", out var hitItems))
            return [];

        var items = new List<TReadModel>();
        foreach (var hit in hitItems.EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var sourceNode))
                continue;

            var item = DeserializeOrNull(sourceNode.GetRawText());
            if (item != null)
                items.Add(item);
        }

        return items;
    }

    private async Task<ElasticsearchDocumentSnapshot?> GetDocumentSnapshotAsync(string keyValue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureIndexAsync(ct);

        using var response = await _httpClient.GetAsync($"{_indexName}/_doc/{Uri.EscapeDataString(keyValue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (IsIndexNotFoundPayload(payload))
                throw BuildMissingIndexException("mutate", payload);
            return null;
        }

        await EnsureSuccessAsync(response, "mutate-get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return null;

        var readModel = DeserializeOrNull(sourceNode.GetRawText());
        if (readModel == null)
            return null;

        if (!jsonDoc.RootElement.TryGetProperty("_seq_no", out var seqNoNode) ||
            !seqNoNode.TryGetInt64(out var seqNo) ||
            !jsonDoc.RootElement.TryGetProperty("_primary_term", out var primaryTermNode) ||
            !primaryTermNode.TryGetInt64(out var primaryTerm))
        {
            throw new InvalidOperationException(
                $"Elasticsearch mutate-get response missing optimistic concurrency metadata for index '{_indexName}' key '{keyValue}'.");
        }

        return new ElasticsearchDocumentSnapshot(readModel, seqNo, primaryTerm);
    }

    private async Task UpsertCoreAsync(
        TReadModel readModel,
        bool allowCreateIndex,
        CancellationToken ct,
        long? ifSeqNo = null,
        long? ifPrimaryTerm = null)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        if (allowCreateIndex)
            await EnsureIndexAsync(ct);

        var keyValue = ResolveReadModelKey(readModel);
        var payload = JsonSerializer.Serialize(readModel, _jsonOptions);
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var requestPath = BuildDocumentRequestPath(keyValue, ifSeqNo, ifPrimaryTerm);
            using var request = new HttpRequestMessage(HttpMethod.Put, requestPath)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var conflictPayload = await response.Content.ReadAsStringAsync(ct);
                throw new ElasticsearchOptimisticConcurrencyException(
                    $"Elasticsearch optimistic concurrency conflict for index '{_indexName}' key '{keyValue}'. body={TruncatePayload(conflictPayload)}");
            }

            await EnsureSuccessAsync(response, "upsert", ct);

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                keyValue,
                elapsedMs,
                "ok");
        }
        catch (Exception ex)
        {
            if (ex is not ElasticsearchOptimisticConcurrencyException)
                LogWriteFailure(keyValue, startedAt, ex);
            throw;
        }
    }

    private string BuildDocumentRequestPath(string keyValue, long? ifSeqNo, long? ifPrimaryTerm)
    {
        var requestPath = $"{_indexName}/_doc/{Uri.EscapeDataString(keyValue)}";
        if (!ifSeqNo.HasValue && !ifPrimaryTerm.HasValue)
            return requestPath;

        if (!ifSeqNo.HasValue || !ifPrimaryTerm.HasValue)
            throw new InvalidOperationException("Elasticsearch optimistic concurrency update requires both seq_no and primary_term.");

        return requestPath + $"?if_seq_no={ifSeqNo.Value}&if_primary_term={ifPrimaryTerm.Value}";
    }

    private bool TryHandleMissingIndexForRead(string operation, string payload)
    {
        if (!IsIndexNotFoundPayload(payload))
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
            $"Configure index bootstrap or set '{nameof(ElasticsearchProjectionReadModelStoreOptions.AutoCreateIndex)}=true'. " +
            $"body={TruncatePayload(payload)}");
    }

    private static bool IsIndexNotFoundPayload(string payload)
    {
        return payload.Contains("index_not_found_exception", StringComparison.OrdinalIgnoreCase);
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

    private string FormatKey(TKey key)
    {
        var keyValue = _keyFormatter(key)?.Trim() ?? "";
        return keyValue;
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

    private string BuildListPayloadJson(int size)
    {
        var sort = _listSortField.Length == 0
            ? BuildDefaultSortSpec()
            : BuildConfiguredSortSpec(_listSortField);

        return JsonSerializer.Serialize(new
        {
            size,
            sort,
            query = new
            {
                match_all = new { },
            },
        });
    }

    private static object[] BuildConfiguredSortSpec(string sortField)
    {
        return
        [
            new Dictionary<string, object>
            {
                [sortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
            new Dictionary<string, object>
            {
                [DefaultListTiebreakSortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
        ];
    }

    private static object[] BuildDefaultSortSpec()
    {
        return
        [
            new Dictionary<string, object>
            {
                [DefaultListPrimarySortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                    ["missing"] = "_last",
                    ["unmapped_type"] = "date",
                },
            },
            new Dictionary<string, object>
            {
                [DefaultListTiebreakSortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
        ];
    }

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (!_autoCreateIndex || _indexInitialized)
            return;

        await _indexInitializationLock.WaitAsync(ct);
        try
        {
            if (_indexInitialized)
                return;

            var payload = BuildIndexInitializationPayload();
            using var request = new HttpRequestMessage(HttpMethod.Put, _indexName)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _indexInitialized = true;
                return;
            }

            var responsePayload = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                responsePayload.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                _indexInitialized = true;
                return;
            }

            throw new InvalidOperationException(
                $"Elasticsearch index initialization failed for '{_indexName}': {(int)response.StatusCode} {response.ReasonPhrase}. body={responsePayload}");
        }
        finally
        {
            _indexInitializationLock.Release();
        }
    }

    private string BuildIndexInitializationPayload()
    {
        var mappings = _indexMetadata.Mappings.Count == 0
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dynamic"] = true,
            }
            : new Dictionary<string, object?>(_indexMetadata.Mappings, StringComparer.Ordinal);

        var root = new Dictionary<string, object?>
        {
            ["mappings"] = mappings,
        };

        if (_indexMetadata.Settings.Count > 0)
            root["settings"] = new Dictionary<string, object?>(_indexMetadata.Settings, StringComparer.Ordinal);
        if (_indexMetadata.Aliases.Count > 0)
            root["aliases"] = new Dictionary<string, object?>(_indexMetadata.Aliases, StringComparer.Ordinal);

        return JsonSerializer.Serialize(root, _jsonOptions);
    }

    private static DocumentIndexMetadata NormalizeMetadata(DocumentIndexMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var normalizedMappings = NormalizeObjectMap(metadata.Mappings, "DocumentIndexMetadata.Mappings");
        var normalizedSettings = NormalizeObjectMap(metadata.Settings, "DocumentIndexMetadata.Settings");
        var normalizedAliases = NormalizeObjectMap(metadata.Aliases, "DocumentIndexMetadata.Aliases");
        return new DocumentIndexMetadata(
            metadata.IndexName?.Trim() ?? "",
            normalizedMappings,
            normalizedSettings,
            normalizedAliases);
    }

    private static Dictionary<string, object?> NormalizeObjectMap(
        IReadOnlyDictionary<string, object?> source,
        string context)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            var key = pair.Key?.Trim() ?? "";
            if (key.Length == 0)
                throw new InvalidOperationException($"{context} contains an empty key.");

            normalized[key] = NormalizeObjectValue(pair.Value, $"{context}['{key}']");
        }

        return normalized;
    }

    private static object? NormalizeObjectValue(object? value, string context)
    {
        if (value == null)
            return null;

        if (value is string ||
            value is bool ||
            value is byte ||
            value is sbyte ||
            value is short ||
            value is ushort ||
            value is int ||
            value is uint ||
            value is long ||
            value is ulong ||
            value is float ||
            value is double ||
            value is decimal)
        {
            return value;
        }

        if (value is JsonElement jsonElement)
            return NormalizeJsonElement(jsonElement, context);

        if (value is IReadOnlyDictionary<string, object?> readonlyObjectMap)
            return NormalizeObjectMap(readonlyObjectMap, context);

        if (value is IDictionary<string, object?> mutableObjectMap)
            return NormalizeObjectMap(
                new Dictionary<string, object?>(mutableObjectMap, StringComparer.Ordinal),
                context);

        if (value is IReadOnlyDictionary<string, string> readonlyStringMap)
        {
            var converted = readonlyStringMap.ToDictionary(
                x => x.Key,
                x => (object?)x.Value,
                StringComparer.Ordinal);
            return NormalizeObjectMap(converted, context);
        }

        if (value is IDictionary<string, string> mutableStringMap)
        {
            var converted = mutableStringMap.ToDictionary(
                x => x.Key,
                x => (object?)x.Value,
                StringComparer.Ordinal);
            return NormalizeObjectMap(converted, context);
        }

        if (value is IEnumerable<object?> objectSequence)
            return objectSequence.Select((x, i) => NormalizeObjectValue(x, $"{context}[{i}]")).ToList();

        if (value is IEnumerable<string> stringSequence)
            return stringSequence.Cast<object?>().ToList();

        throw new InvalidOperationException(
            $"{context} contains unsupported value type '{value.GetType().FullName}'.");
    }

    private static object? NormalizeJsonElement(JsonElement element, string context)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element
                .EnumerateObject()
                .ToDictionary(
                    x => x.Name,
                    x => NormalizeJsonElement(x.Value, $"{context}['{x.Name}']"),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element
                .EnumerateArray()
                .Select((x, i) => NormalizeJsonElement(x, $"{context}[{i}]"))
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NormalizeJsonNumber(element, context),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException(
                $"{context} contains unsupported json value kind '{element.ValueKind}'."),
        };
    }

    private static object NormalizeJsonNumber(JsonElement numberElement, string context)
    {
        if (numberElement.TryGetInt64(out var int64Value))
            return int64Value;
        if (numberElement.TryGetDecimal(out var decimalValue))
            return decimalValue;
        if (numberElement.TryGetDouble(out var doubleValue))
            return doubleValue;

        throw new InvalidOperationException($"{context} contains an invalid JSON number value.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var payload = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Elasticsearch {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. body={payload}");
    }

    private static Uri ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("Elasticsearch provider requires at least one endpoint.");

        var endpoint = endpoints[0].Trim();
        if (endpoint.Length == 0)
            throw new InvalidOperationException("Elasticsearch endpoint cannot be empty.");
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid Elasticsearch endpoint '{endpoints[0]}'.");

        return uri;
    }

    private static string BuildIndexName(string indexPrefix, string indexScope)
    {
        var prefix = NormalizeToken(indexPrefix);
        if (prefix.Length == 0)
            prefix = "aevatar";
        return $"{prefix}-{indexScope}";
    }

    private static string NormalizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        var chars = token
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string TruncatePayload(string payload)
    {
        const int maxLength = 512;
        if (payload.Length <= maxLength)
            return payload;

        return payload[..maxLength] + "...(truncated)";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _indexInitializationLock.Dispose();
    }

    private sealed record ElasticsearchDocumentSnapshot(TReadModel ReadModel, long SeqNo, long PrimaryTerm);

    private sealed class ElasticsearchOptimisticConcurrencyException : InvalidOperationException
    {
        public ElasticsearchOptimisticConcurrencyException(string message)
            : base(message)
        {
        }
    }
}
