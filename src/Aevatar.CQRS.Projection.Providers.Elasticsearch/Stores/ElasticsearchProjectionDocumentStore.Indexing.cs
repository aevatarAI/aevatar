using System.Net;
using System.Text;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed partial class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
{
    private async Task EnsureIndexAsync(
        string indexName,
        DocumentIndexMetadata metadata,
        CancellationToken ct)
    {
        if (!_autoCreateIndex)
            return;

        await _indexInitializationLock.WaitAsync(ct);
        try
        {
            lock (_dynamicIndexStateGate)
            {
                if (_initializedIndices.Contains(indexName))
                    return;
            }

            var payload = ElasticsearchProjectionDocumentStorePayloadSupport.BuildIndexInitializationPayload(
                metadata);
            using var request = new HttpRequestMessage(HttpMethod.Put, indexName)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                MarkIndexInitialized(indexName);
                return;
            }

            var responsePayload = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                responsePayload.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                MarkIndexInitialized(indexName);
                return;
            }

            throw new InvalidOperationException(
                $"Elasticsearch index initialization failed for '{indexName}': {(int)response.StatusCode} {response.ReasonPhrase}. body={responsePayload}");
        }
        finally
        {
            _indexInitializationLock.Release();
        }
    }

    private void MarkIndexInitialized(string indexName)
    {
        lock (_dynamicIndexStateGate)
            _initializedIndices.Add(indexName);
    }
}
