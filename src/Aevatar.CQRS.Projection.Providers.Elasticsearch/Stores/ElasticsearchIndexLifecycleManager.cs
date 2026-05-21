using System.Net;
using System.Text;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal sealed class ElasticsearchIndexLifecycleManager : IDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _mappingProbeLock = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _initializedIndices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _actualFieldMappingsByIndex =
        new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly bool _autoCreate;

    public ElasticsearchIndexLifecycleManager(HttpClient httpClient, bool autoCreate)
    {
        _httpClient = httpClient;
        _autoCreate = autoCreate;
    }

    public async Task EnsureIndexAsync(
        string indexName,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        if (!_autoCreate)
            return;

        lock (_stateGate)
        {
            if (_initializedIndices.Contains(indexName))
                return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            lock (_stateGate)
            {
                if (_initializedIndices.Contains(indexName))
                    return;
            }

            var payload = ElasticsearchProjectionDocumentStorePayloadSupport.BuildIndexInitializationPayload(metadata);
            using var request = new HttpRequestMessage(HttpMethod.Put, indexName)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                MarkInitialized(indexName);
                return;
            }

            var responsePayload = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                responsePayload.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                MarkInitialized(indexName);
                return;
            }

            throw new InvalidOperationException(
                $"Elasticsearch index initialization failed for '{indexName}': {(int)response.StatusCode} {response.ReasonPhrase}. body={responsePayload}");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void MarkInitialized(string indexName)
    {
        lock (_stateGate)
            _initializedIndices.Add(indexName);
    }

    /// <summary>
    /// Reads the live Elasticsearch <c>_mapping</c> for an index so the query path can resolve
    /// keyword/text field paths from physical truth rather than code-side augmented metadata.
    /// Returns <c>null</c> when the index is absent or the mapping cannot be read; callers then
    /// fall back to declared metadata. Successful reads are cached for the manager lifetime.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>?> GetActualFieldMappingsAsync(
        string indexName,
        CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_actualFieldMappingsByIndex.TryGetValue(indexName, out var cached))
                return cached;
        }

        await _mappingProbeLock.WaitAsync(ct);
        try
        {
            lock (_stateGate)
            {
                if (_actualFieldMappingsByIndex.TryGetValue(indexName, out var cached))
                    return cached;
            }

            var mappings = await ReadActualFieldMappingsAsync(indexName, ct);
            if (mappings == null)
                return null;

            lock (_stateGate)
                _actualFieldMappingsByIndex[indexName] = mappings;
            return mappings;
        }
        finally
        {
            _mappingProbeLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>?> ReadActualFieldMappingsAsync(
        string indexName,
        CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{indexName}/_mapping", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadAsStringAsync(ct);
            return ElasticsearchProjectionDocumentStoreMetadataSupport
                .TryExtractFieldMappingsFromMappingResponse(payload, indexName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Best-effort probe: an unreachable mapping endpoint or HTTP timeout must not fail the
            // query. The caller falls back to declared metadata (pre-existing resolution behaviour).
            return null;
        }
    }

    public void Dispose()
    {
        _initLock.Dispose();
        _mappingProbeLock.Dispose();
    }
}
