using System.Net;
using System.Text;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed partial class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
{
    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (!_autoCreateIndex || _indexInitialized)
            return;

        await _indexInitializationLock.WaitAsync(ct);
        try
        {
            if (_indexInitialized)
                return;

            var payload = ElasticsearchProjectionDocumentStorePayloadSupport.BuildIndexInitializationPayload(
                _indexMetadata,
                _jsonOptions);
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
}
