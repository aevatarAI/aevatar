namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDocumentStoreHttpSupport
{
    internal static async Task EnsureSuccessAsync(
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

    internal static bool IsIndexNotFoundPayload(string payload)
    {
        return payload.Contains("index_not_found_exception", StringComparison.OrdinalIgnoreCase);
    }
}
