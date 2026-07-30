using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
    : IProjectionDocumentReader<TReadModel, TKey>,
      IProjectionDocumentWriter<TReadModel>,
      IProjectionIndexConsistencyProbe<TReadModel>,
      IProjectionIndexReconcileTarget,
      IDisposable
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    private const string ProviderName = "Elasticsearch";

    private readonly JsonFormatter _formatter;
    private readonly JsonParser _parser;
    private readonly HttpClient _httpClient;
    private readonly ElasticsearchIndexLifecycleManager _indexManager;
    private readonly ElasticsearchOptimisticWriter<TReadModel> _writer;
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly string _indexPrefix;
    private readonly string _indexName;
    private readonly int _queryTakeMax;
    private readonly bool _autoCreateIndex;
    private readonly string _defaultSortField;
    private readonly ElasticsearchMissingIndexBehavior _missingIndexBehavior;
    private readonly TimeSpan _repairRequestTimeout;
    private readonly bool _supportsDynamicIndexing;
    private readonly DocumentIndexMetadata _indexMetadata;
    private readonly Func<TReadModel, string?>? _indexScopeSelector;
    private readonly Func<string, string> _fieldPathResolver;
    private readonly Func<ProjectionDocumentFilter, string, string> _exactMatchFieldPathResolver;
    private readonly ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>> _logger;

    public ElasticsearchProjectionDocumentStore(
        ElasticsearchProjectionDocumentStoreOptions options,
        DocumentIndexMetadata indexMetadata,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, string?>? indexScopeSelector = null,
        TypeRegistry? typeRegistry = null,
        ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>? logger = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySelector);

        var registry = typeRegistry ?? BuildDefaultTypeRegistry();
        _formatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true)
                .WithTypeRegistry(registry));
        _parser = new JsonParser(
            JsonParser.Settings.Default
                .WithIgnoreUnknownFields(true)
                .WithTypeRegistry(registry));

        var endpoint = ElasticsearchProjectionDocumentStoreNamingSupport.ResolvePrimaryEndpoint(options.Endpoints);
        _httpClient = httpMessageHandler == null
            ? new HttpClient()
            : new HttpClient(httpMessageHandler, disposeHandler: true);
        _httpClient.BaseAddress = endpoint;
        _repairRequestTimeout = TimeSpan.FromMilliseconds(Math.Max(500, options.RequestTimeoutMs));
        _httpClient.Timeout = _repairRequestTimeout;

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = $"{options.Username}:{options.Password}";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        var descriptor = new TReadModel().Descriptor;
        var normalizedMetadata = ElasticsearchProjectionDocumentStoreMetadataSupport.NormalizeMetadata(indexMetadata);
        var augmentedMetadata = ElasticsearchProjectionDescriptorMappingSupport.AugmentMetadata(
            normalizedMetadata,
            descriptor);
        var finalMetadata = ElasticsearchProjectionDocumentStoreMetadataSupport.NormalizeMetadata(augmentedMetadata);
        _indexPrefix = options.IndexPrefix?.Trim() ?? "";
        var normalizedScope = ElasticsearchProjectionDocumentStoreNamingSupport.NormalizeToken(finalMetadata.IndexName);
        if (normalizedScope.Length == 0)
            normalizedScope = "readmodel";
        _indexName = ElasticsearchProjectionDocumentStoreNamingSupport.BuildIndexName(_indexPrefix, normalizedScope);
        _queryTakeMax = options.QueryTakeMax > 0 ? options.QueryTakeMax : 200;
        _autoCreateIndex = options.AutoCreateIndex;
        _missingIndexBehavior = options.MissingIndexBehavior;
        _supportsDynamicIndexing = indexScopeSelector is not null;
        _indexMetadata = finalMetadata with { IndexName = _indexName };
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _indexScopeSelector = indexScopeSelector;
        _defaultSortField = options.DefaultSortField?.Trim() ?? "";
        _fieldPathResolver = BuildFieldPathResolver(descriptor);
        _exactMatchFieldPathResolver = BuildExactMatchFieldPathResolver(descriptor, _indexMetadata);
        _logger = logger ?? NullLogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>.Instance;

        _indexManager = new ElasticsearchIndexLifecycleManager(_httpClient, _autoCreateIndex, _logger);
        _writer = new ElasticsearchOptimisticWriter<TReadModel>(
            _httpClient, _formatter, _parser, _autoCreateIndex, _missingIndexBehavior, _logger);
    }

    public async Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var indexTarget = ResolveIndexTarget(readModel);
        await _indexManager.EnsureIndexAsync(indexTarget.IndexName, indexTarget.Metadata, ct);
        var keyValue = ResolveReadModelKey(readModel);
        return await _writer.UpsertAsync(indexTarget.IndexName, keyValue, readModel, ct);
    }

    public async Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelWritesUnsupportedForDelete();

        var trimmedId = id.Trim();
        // Refactor (iter89/cluster-089-projection-provider-elapsed-clock):
        // Old: elapsedMs used DateTimeOffset.UtcNow subtraction, so wall-clock changes could skew duration logs.
        // New: elapsedMs uses a monotonic Stopwatch timestamp; projection clocks remain for semantic timestamps only.
        var startedAtTimestamp = Stopwatch.GetTimestamp();
        try
        {
            using var response = await _httpClient.DeleteAsync(
                $"{_indexName}/_doc/{Uri.EscapeDataString(trimmedId)}",
                ct);
            ProjectionWriteResult result;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
                TryHandleMissingIndexForDelete(notFoundPayload);
                result = ProjectionWriteResult.Duplicate();
            }
            else
            {
                await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "delete", ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                result = ResolveDeleteResultFromPayload(payload);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model delete completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                trimmedId,
                elapsedMs,
                result.Disposition);
            return result;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model delete failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                trimmedId,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }

    public async Task<ProjectionWriteResult> DeleteAsync(
        ProjectionDocumentDeleteMarker marker,
        CancellationToken ct = default)
    {
        marker = ElasticsearchProjectionDeleteMarkerPayload.Normalize(marker);
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelWritesUnsupportedForDelete();

        await _indexManager.EnsureIndexAsync(_indexName, _indexMetadata, ct);
        var startedAtTimestamp = Stopwatch.GetTimestamp();
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var existing = await TryGetExistingProjectionStateAsync(_indexName, marker.Id, ct);
                var result = EvaluateDeleteMarker(existing, marker);
                if (!result.IsApplied)
                {
                    LogVersionedDeleteSkipped(marker, startedAtTimestamp, result);
                    return result;
                }

                var payload = ElasticsearchProjectionDeleteMarkerPayload.Serialize(marker, marker.Id);
                using var request = BuildConditionalTombstoneRequest(_indexName, marker.Id, payload, existing);
                using var response = await _httpClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    LogVersionedDeleteCompleted(marker, startedAtTimestamp);
                    return ProjectionWriteResult.Applied();
                }

                if (response.StatusCode != HttpStatusCode.Conflict)
                    await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "versioned delete", ct);

                _logger.LogInformation(
                    "Projection read-model delete hit optimistic concurrency conflict and will re-evaluate. provider={Provider} readModelType={ReadModelType} key={Key} attempt={Attempt}/{MaxAttempts}",
                    ProviderName,
                    typeof(TReadModel).FullName,
                    marker.Id,
                    attempt,
                    3);
            }

            var reconciled = await TryGetExistingProjectionStateAsync(_indexName, marker.Id, ct);
            var reconciledResult = EvaluateDeleteMarker(reconciled, marker);
            if (!reconciledResult.IsApplied)
            {
                LogVersionedDeleteSkipped(marker, startedAtTimestamp, reconciledResult);
                return reconciledResult;
            }

            throw new InvalidOperationException(
                $"Elasticsearch optimistic concurrency delete could not be reconciled for read-model '{typeof(TReadModel).FullName}' key '{marker.Id}'.");
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model versioned delete failed. provider={Provider} readModelType={ReadModelType} key={Key} stateVersion={StateVersion} lastEventId={LastEventId} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                marker.Id,
                marker.StateVersion,
                marker.LastEventId,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }

    public async Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("get");
        await EnsureReadIndexConsistentAsync(ct);

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

    internal async Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?> InspectRepairAsync(
        TKey key,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("repair inspection");

        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
            return null;

        return await ReadRepairLeaseAsync(key, keyValue, _indexName, ct);
    }

    internal async Task<ElasticsearchProjectionDocumentRepairDeleteDisposition>
        DeleteRepairIfUnchangedCoreAsync(
        ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey> lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        var keyValue = FormatKey(lease.Key);
        if (keyValue.Length == 0)
        {
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for Elasticsearch repair deletion.");
        }

        using var response = await _httpClient.DeleteAsync(
            $"{lease.ConcreteIndexName}/_doc/{Uri.EscapeDataString(keyValue)}" +
            $"?if_seq_no={lease.SequenceNumber}&if_primary_term={lease.PrimaryTerm}",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (IsExactRepairDocumentNotFound(
                    payload,
                    lease.ConcreteIndexName,
                    keyValue,
                    deleteResponse: true))
            {
                return ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent;
            }

            throw ElasticsearchProjectionDocumentStoreHttpSupport.CreateFailure(
                response,
                "repair-delete",
                payload);
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
            return ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict;

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(
            response,
            "repair-delete",
            ct);
        return ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted;
    }

    internal Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?>
        InspectRepairLeaseRevisionAsync(
            ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey> lease,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var keyValue = FormatKey(lease.Key);
        if (keyValue.Length == 0)
        {
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for Elasticsearch repair inspection.");
        }

        return ReadRepairLeaseAsync(lease.Key, keyValue, lease.ConcreteIndexName, ct);
    }

    internal TimeSpan RepairRequestTimeout => _repairRequestTimeout;

    public async Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("query");
        await EnsureReadIndexConsistentAsync(ct);
        var boundedTake = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, _queryTakeMax);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_indexName}/_search")
        {
            Content = new StringContent(
                ElasticsearchProjectionDocumentStorePayloadSupport.BuildQueryPayloadJson(
                    query,
                    _defaultSortField,
                    boundedTake,
                    _fieldPathResolver,
                    _exactMatchFieldPathResolver),
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

    public async Task<ProjectionIndexConsistencyResult> CheckIndexConsistencyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("index consistency probe");
        return await _indexManager.CheckConsistencyAsync(_indexName, _indexMetadata, ct);
    }

    /// <inheritdoc />
    public string IndexAlias => _indexName;

    /// <inheritdoc />
    public async Task ReconcileIndexAsync(CancellationToken ct = default)
    {
        // No-op when auto-create is off (operator owns the lifecycle) or when the store uses
        // dynamic per-document indexing (no single static physical to reconcile).
        if (!_autoCreateIndex || _supportsDynamicIndexing)
            return;

        await _indexManager.ReconcileWithReindexAsync(_indexName, _indexMetadata, ct);
    }

    private async Task EnsureReadIndexConsistentAsync(CancellationToken ct)
    {
        if (!_autoCreateIndex)
            return;

        var consistency = await _indexManager.CheckConsistencyAsync(_indexName, _indexMetadata, ct);
        if (consistency.IsConsistent || consistency.Status == ProjectionIndexConsistencyStatus.Missing)
            return;

        throw new ProjectionIndexSchemaDriftException(
            consistency.Provider,
            consistency.IndexAlias,
            consistency.CurrentPhysicalIndex ?? consistency.IndexAlias,
            consistency.ExpectedPhysicalIndex);
    }

    private async Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?> ReadRepairLeaseAsync(
        TKey key,
        string keyValue,
        string indexName,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            $"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
            if (IsExactRepairDocumentNotFound(
                    notFoundPayload,
                    indexName,
                    keyValue,
                    deleteResponse: false))
            {
                return null;
            }

            throw ElasticsearchProjectionDocumentStoreHttpSupport.CreateFailure(
                response,
                "repair-inspect",
                notFoundPayload);
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(
            response,
            "repair-inspect",
            ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(payload);
        var root = jsonDoc.RootElement;
        var concreteIndexName = ReadRequiredString(root, "_index");
        var sequenceNumber = ReadRequiredLong(root, "_seq_no");
        var primaryTerm = ReadRequiredLong(root, "_primary_term");
        if (!root.TryGetProperty("_source", out var sourceNode))
        {
            throw new InvalidOperationException(
                $"Elasticsearch repair inspection did not return '_source' for read-model '{typeof(TReadModel).FullName}' key '{keyValue}'.");
        }

        var document = DeserializeOrNull(sourceNode.GetRawText())
                       ?? throw new InvalidOperationException(
                           $"Elasticsearch repair inspection returned an invalid read-model '{typeof(TReadModel).FullName}' for key '{keyValue}'.");
        return new ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>(
            key,
            document,
            concreteIndexName,
            sequenceNumber,
            primaryTerm);
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidOperationException(
            $"Elasticsearch repair inspection response is missing required string property '{propertyName}'.");
    }

    private static long ReadRequiredLong(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out var value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Elasticsearch repair inspection response is missing required integer property '{propertyName}'.");
    }

    private static bool IsExactRepairDocumentNotFound(
        string payload,
        string expectedIndexName,
        string expectedDocumentId,
        bool deleteResponse)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(payload);
            var root = jsonDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("_index", out var indexNode) ||
                indexNode.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    indexNode.GetString(),
                    expectedIndexName,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("_id", out var idNode) ||
                idNode.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    idNode.GetString(),
                    expectedDocumentId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (deleteResponse)
            {
                return root.TryGetProperty("result", out var resultNode) &&
                       resultNode.ValueKind == JsonValueKind.String &&
                       string.Equals(
                           resultNode.GetString(),
                           "not_found",
                           StringComparison.Ordinal);
            }

            return root.TryGetProperty("found", out var foundNode) &&
                   foundNode.ValueKind is JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private string FormatKey(TKey key) => _keyFormatter(key)?.Trim() ?? "";

    private static Func<string, string> BuildFieldPathResolver(MessageDescriptor descriptor)
    {
        return fieldPath => ResolveFieldPath(descriptor, fieldPath);
    }

    private static Func<ProjectionDocumentFilter, string, string> BuildExactMatchFieldPathResolver(
        MessageDescriptor descriptor,
        DocumentIndexMetadata indexMetadata)
    {
        var descriptorFieldMap = BuildDescriptorFieldMap(descriptor);
        return (filter, resolvedFieldPath) =>
        {
            if (resolvedFieldPath.EndsWith(".keyword", StringComparison.Ordinal))
                return resolvedFieldPath;

            if (filter.Value.Kind is not ProjectionDocumentValueKind.String and not ProjectionDocumentValueKind.StringList)
                return resolvedFieldPath;

            if (ElasticsearchProjectionDocumentStoreMetadataSupport.TryGetFieldMapping(
                    indexMetadata.Mappings,
                    resolvedFieldPath,
                    out var explicitMapping))
            {
                if (ElasticsearchProjectionDocumentStoreMetadataSupport.IsKeywordFieldMapping(explicitMapping))
                    return resolvedFieldPath;

                if (ElasticsearchProjectionDocumentStoreMetadataSupport.HasKeywordMultiField(explicitMapping))
                    return $"{resolvedFieldPath}.keyword";

                return resolvedFieldPath;
            }

            return descriptorFieldMap.TryGetValue(resolvedFieldPath, out var field) &&
                   field.FieldType == FieldType.String
                ? $"{resolvedFieldPath}.keyword"
                : resolvedFieldPath;
        };
    }

    private static string ResolveFieldPath(MessageDescriptor descriptor, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
            return fieldPath;

        var segments = fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return fieldPath;

        var resolvedSegments = new string[segments.Length];
        MessageDescriptor? currentDescriptor = descriptor;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var suffix = "";
            if (segment.EndsWith("[]", StringComparison.Ordinal))
            {
                segment = segment[..^2];
                suffix = "[]";
            }

            if (currentDescriptor == null)
            {
                resolvedSegments[index] = $"{segment}{suffix}";
                continue;
            }

            var field = ResolveField(currentDescriptor, segment);
            if (field == null)
            {
                resolvedSegments[index] = $"{segment}{suffix}";
                currentDescriptor = null;
                continue;
            }

            resolvedSegments[index] = $"{field.Name}{suffix}";
            currentDescriptor = field.FieldType == FieldType.Message
                ? field.MessageType
                : null;
        }

        return string.Join(".", resolvedSegments);
    }

    private static FieldDescriptor? ResolveField(MessageDescriptor descriptor, string segment)
    {
        if (segment.Length == 0)
            return null;

        var candidates = BuildFieldCandidates(segment);
        return descriptor.Fields.InDeclarationOrder().FirstOrDefault(field =>
            candidates.Contains(field.Name) ||
            candidates.Contains(field.JsonName) ||
            candidates.Contains(field.PropertyName));
    }

    private static HashSet<string> BuildFieldCandidates(string segment)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            segment,
        };

        var snakeCase = ToSnakeCase(segment);
        if (snakeCase.Length > 0)
        {
            candidates.Add(snakeCase);
            candidates.Add($"{snakeCase}_utc_value");

            if (snakeCase.EndsWith("s", StringComparison.Ordinal) && snakeCase.Length > 1)
                candidates.Add($"{snakeCase[..^1]}_entries");
        }

        if (segment.EndsWith("At", StringComparison.Ordinal) && segment.Length > 2)
            candidates.Add($"{segment}UtcValue");

        return candidates;
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current))
            {
                if (index > 0)
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static Dictionary<string, FieldDescriptor> BuildDescriptorFieldMap(MessageDescriptor descriptor)
    {
        var fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal);
        VisitDescriptorFields(descriptor, prefix: null, fields, new HashSet<MessageDescriptor>());
        return fields;
    }

    private static void VisitDescriptorFields(
        MessageDescriptor descriptor,
        string? prefix,
        Dictionary<string, FieldDescriptor> fields,
        HashSet<MessageDescriptor> ancestry)
    {
        if (!ancestry.Add(descriptor))
            return;

        try
        {
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                var path = string.IsNullOrWhiteSpace(prefix)
                    ? field.Name
                    : $"{prefix}.{field.Name}";
                fields[path] = field;

                if (field.FieldType == FieldType.Message && field.MessageType != null)
                    VisitDescriptorFields(field.MessageType, path, fields, ancestry);
            }
        }
        finally
        {
            ancestry.Remove(descriptor);
        }
    }

    private TReadModel? DeserializeOrNull(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (ElasticsearchProjectionDeleteMarkerPayload.IsDeleteMarker(document.RootElement))
                return null;

            return _parser.Parse<TReadModel>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Projection read-model deserialization failed. provider={Provider} readModelType={ReadModelType} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                "ignored",
                ex.GetType().Name);
            return null;
        }
    }

    private bool TryHandleMissingIndexForRead(string operation, string payload)
    {
        if (!ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(payload))
            return false;

        if (_autoCreateIndex || _missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
            throw new InvalidOperationException(
                $"Elasticsearch index '{_indexName}' was not found during '{operation}' for read-model '{typeof(TReadModel).FullName}'. " +
                $"Configure index bootstrap or set '{nameof(ElasticsearchProjectionDocumentStoreOptions.AutoCreateIndex)}=true'. " +
                $"body={ElasticsearchProjectionDocumentStoreNamingSupport.TruncatePayload(payload)}");

        _logger.LogWarning(
            "Projection read-model index is missing. provider={Provider} readModelType={ReadModelType} index={Index} operation={Operation} behavior={Behavior}",
            ProviderName,
            typeof(TReadModel).FullName,
            _indexName,
            operation,
            _missingIndexBehavior);
        return true;
    }

    private void TryHandleMissingIndexForDelete(string payload)
    {
        if (!ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(payload))
            return;

        if (_missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
            throw new InvalidOperationException(
                $"Elasticsearch index '{_indexName}' was not found during 'delete' for read-model '{typeof(TReadModel).FullName}'. " +
                $"Configure index bootstrap before issuing deletes. body={ElasticsearchProjectionDocumentStoreNamingSupport.TruncatePayload(payload)}");

        _logger.LogWarning(
            "Projection read-model index is missing during delete. provider={Provider} readModelType={ReadModelType} index={Index} behavior={Behavior}",
            ProviderName,
            typeof(TReadModel).FullName,
            _indexName,
            _missingIndexBehavior);
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

    private static ProjectionWriteResult ResolveDeleteResultFromPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ProjectionWriteResult.Applied();

        try
        {
            using var jsonDoc = JsonDocument.Parse(payload);
            if (jsonDoc.RootElement.TryGetProperty("result", out var resultNode) &&
                resultNode.ValueKind == JsonValueKind.String &&
                string.Equals(resultNode.GetString(), "not_found", StringComparison.Ordinal))
            {
                return ProjectionWriteResult.Duplicate();
            }
        }
        catch (JsonException)
        {
        }

        return ProjectionWriteResult.Applied();
    }

    private void ThrowIfDynamicReadModelWritesUnsupportedForDelete()
    {
        if (!_supportsDynamicIndexing)
            return;

        throw new InvalidOperationException(
            $"Elasticsearch 'delete' by key is not supported for dynamically indexed read model '{typeof(TReadModel).FullName}'. " +
            "Dynamically indexed read models must delete via provider-native index-scoped operations.");
    }

    private async Task<ExistingProjectionState> TryGetExistingProjectionStateAsync(
        string indexName,
        string keyValue,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync($"{indexName}/_doc/{Uri.EscapeDataString(keyValue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
            if (ElasticsearchProjectionDocumentStoreHttpSupport.IsIndexNotFoundPayload(notFoundPayload))
            {
                if (_autoCreateIndex || _missingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw)
                    throw new InvalidOperationException(
                        $"Elasticsearch index '{indexName}' was not found during 'get' for read-model '{typeof(TReadModel).FullName}'.");

                return ExistingProjectionState.Missing;
            }

            return ExistingProjectionState.Missing;
        }

        await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "get", ct);
        var successfulPayload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(successfulPayload);
        var seqNo = TryReadLong(jsonDoc.RootElement, "_seq_no");
        var primaryTerm = TryReadLong(jsonDoc.RootElement, "_primary_term");
        if (!jsonDoc.RootElement.TryGetProperty("_source", out var sourceNode))
            return new ExistingProjectionState(null, null, seqNo, primaryTerm);

        var deleteMarker = ElasticsearchProjectionDeleteMarkerPayload.TryParse(sourceNode);
        if (deleteMarker != null)
            return new ExistingProjectionState(null, deleteMarker, seqNo, primaryTerm);

        return new ExistingProjectionState(DeserializeOrNull(sourceNode.GetRawText()), null, seqNo, primaryTerm);
    }

    private static ProjectionWriteResult EvaluateDeleteMarker(
        ExistingProjectionState existing,
        ProjectionDocumentDeleteMarker marker)
    {
        if (existing.ReadModel != null)
            return ProjectionWriteResultEvaluator.Evaluate(existing.ReadModel, marker);

        if (existing.DeleteMarker == null)
            return ProjectionWriteResult.Applied();

        var result = ElasticsearchProjectionDeleteMarkerPayload.EvaluateUpsertAgainstDeleteMarker(
            existing.DeleteMarker,
            marker);
        return result.Disposition == ProjectionWriteDisposition.Duplicate
            ? ProjectionWriteResult.Duplicate()
            : result;
    }

    private static HttpRequestMessage BuildConditionalTombstoneRequest(
        string indexName,
        string keyValue,
        string payload,
        ExistingProjectionState existing)
    {
        var requestPath = existing.ReadModel == null && existing.DeleteMarker == null
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

    private void LogVersionedDeleteCompleted(
        ProjectionDocumentDeleteMarker marker,
        long startedAtTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "Projection read-model versioned delete completed. provider={Provider} readModelType={ReadModelType} key={Key} stateVersion={StateVersion} lastEventId={LastEventId} elapsedMs={ElapsedMs} result={Result}",
            ProviderName,
            typeof(TReadModel).FullName,
            marker.Id,
            marker.StateVersion,
            marker.LastEventId,
            elapsedMs,
            ProjectionWriteDisposition.Applied);
    }

    private void LogVersionedDeleteSkipped(
        ProjectionDocumentDeleteMarker marker,
        long startedAtTimestamp,
        ProjectionWriteResult result)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAtTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "Projection read-model versioned delete skipped. provider={Provider} readModelType={ReadModelType} key={Key} stateVersion={StateVersion} lastEventId={LastEventId} elapsedMs={ElapsedMs} result={Result}",
            ProviderName,
            typeof(TReadModel).FullName,
            marker.Id,
            marker.StateVersion,
            marker.LastEventId,
            elapsedMs,
            result.Disposition);
    }

    private static TypeRegistry BuildDefaultTypeRegistry()
    {
        var descriptors = new List<MessageDescriptor>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;

            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || !type.IsClass)
                    continue;

                var descriptorProperty = type.GetProperty(
                    "Descriptor",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    typeof(MessageDescriptor),
                    Type.EmptyTypes,
                    null);

                if (descriptorProperty?.GetValue(null) is MessageDescriptor descriptor)
                    descriptors.Add(descriptor);
            }
        }

        return TypeRegistry.FromMessages(descriptors);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _indexManager.Dispose();
    }

    private sealed record ResolvedIndexTarget(string IndexName, DocumentIndexMetadata Metadata);

    private sealed record ExistingProjectionState(
        TReadModel? ReadModel,
        ProjectionDocumentDeleteMarker? DeleteMarker,
        long SeqNo,
        long PrimaryTerm)
    {
        public static ExistingProjectionState Missing { get; } = new(null, null, -1, -1);
    }
}
