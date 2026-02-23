using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed class ElasticsearchProjectionReadModelStore<TReadModel, TKey>
    : IProjectionReadModelStore<TReadModel, TKey>,
      IProjectionReadModelStoreProviderMetadata,
      IDisposable
    where TReadModel : class
{
    private readonly HttpClient _httpClient;
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly string _indexName;
    private readonly int _listTakeMax;
    private readonly bool _autoCreateIndex;
    private readonly string _listSortField;
    private readonly SemaphoreSlim _indexInitializationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private bool _indexInitialized;

    public ElasticsearchProjectionReadModelStore(
        ElasticsearchProjectionReadModelStoreOptions options,
        string indexScope,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionReadModelProviderNames.Elasticsearch)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySelector);

        var endpoint = ResolvePrimaryEndpoint(options.Endpoints);
        _httpClient = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromMilliseconds(Math.Max(500, options.RequestTimeoutMs)),
        };

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = $"{options.Username}:{options.Password}";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        var normalizedScope = NormalizeToken(indexScope);
        if (normalizedScope.Length == 0)
            normalizedScope = "readmodel";

        _indexName = BuildIndexName(options.IndexPrefix, normalizedScope);
        _listTakeMax = options.ListTakeMax > 0 ? options.ListTakeMax : 200;
        _autoCreateIndex = options.AutoCreateIndex;
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _listSortField = options.ListSortField?.Trim() ?? "";
        ProviderCapabilities = BuildCapabilities(providerName);
    }

    public ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default) =>
        UpsertCoreAsync(readModel, allowCreateIndex: true, ct);

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        var existing = await GetAsync(key, ct);
        if (existing == null)
            throw new InvalidOperationException($"ReadModel '{typeof(TReadModel).FullName}' with key '{FormatKey(key)}' was not found.");

        mutate(existing);
        await UpsertCoreAsync(existing, allowCreateIndex: true, ct);
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
            return null;

        await EnsureSuccessAsync(response, "get", ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(payload);
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
            return [];

        await EnsureSuccessAsync(response, "list", ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(payload);
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

    private async Task UpsertCoreAsync(TReadModel readModel, bool allowCreateIndex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        if (allowCreateIndex)
            await EnsureIndexAsync(ct);

        var keyValue = ResolveReadModelKey(readModel);
        var payload = JsonSerializer.Serialize(readModel, _jsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{_indexName}/_doc/{Uri.EscapeDataString(keyValue)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "upsert", ct);
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
        if (_listSortField.Length == 0)
            return JsonSerializer.Serialize(new { size, query = new { match_all = new { } } });

        return JsonSerializer.Serialize(new
        {
            size,
            sort = new object[]
            {
                new Dictionary<string, object>
                {
                    [_listSortField] = new { order = "desc" },
                },
            },
            query = new
            {
                match_all = new { },
            },
        });
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

            using var request = new HttpRequestMessage(HttpMethod.Put, _indexName)
            {
                Content = new StringContent("{\"mappings\":{\"dynamic\":true}}", Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _indexInitialized = true;
                return;
            }

            var payload = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                payload.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                _indexInitialized = true;
                return;
            }

            throw new InvalidOperationException(
                $"Elasticsearch index initialization failed for '{_indexName}': {(int)response.StatusCode} {response.ReasonPhrase}. body={payload}");
        }
        finally
        {
            _indexInitializationLock.Release();
        }
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

    private static ProjectionReadModelProviderCapabilities BuildCapabilities(string providerName) =>
        new(
            providerName,
            supportsIndexing: true,
            indexKinds: [ProjectionReadModelIndexKind.Document],
            supportsAliases: true,
            supportsSchemaValidation: true);

    public void Dispose()
    {
        _httpClient.Dispose();
        _indexInitializationLock.Dispose();
    }
}
