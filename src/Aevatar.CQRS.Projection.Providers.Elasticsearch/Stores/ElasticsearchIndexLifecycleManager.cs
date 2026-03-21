using System.Net;
using System.Text;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal sealed class ElasticsearchIndexLifecycleManager : IDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _initializedIndices = new(StringComparer.Ordinal);
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

    public void Dispose() => _initLock.Dispose();
}
