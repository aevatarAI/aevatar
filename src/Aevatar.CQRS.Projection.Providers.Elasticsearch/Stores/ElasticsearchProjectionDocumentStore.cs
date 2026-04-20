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
        var descriptor = new TReadModel().Descriptor;
        _fieldPathResolver = BuildFieldPathResolver(descriptor);
        _exactMatchFieldPathResolver = BuildExactMatchFieldPathResolver(descriptor, _indexMetadata);
        _logger = logger ?? NullLogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>.Instance;

        _indexManager = new ElasticsearchIndexLifecycleManager(_httpClient, _autoCreateIndex);
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
        await _indexManager.EnsureIndexAsync(_indexName, _indexMetadata, ct);

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            using var response = await _httpClient.DeleteAsync(
                $"{_indexName}/_doc/{Uri.EscapeDataString(trimmedId)}",
                ct);
            ProjectionWriteResult result;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
                TryHandleMissingIndexForRead("delete", notFoundPayload);
                result = ProjectionWriteResult.Duplicate();
            }
            else
            {
                await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(response, "delete", ct);
                var payload = await response.Content.ReadAsStringAsync(ct);
                result = ResolveDeleteResultFromPayload(payload);
            }

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
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
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
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

    public async Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDynamicReadModelQueriesUnsupported("get");
        await _indexManager.EnsureIndexAsync(_indexName, _indexMetadata, ct);

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
        await _indexManager.EnsureIndexAsync(_indexName, _indexMetadata, ct);
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
            return _parser.Parse<TReadModel>(json);
        }
        catch
        {
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
}
