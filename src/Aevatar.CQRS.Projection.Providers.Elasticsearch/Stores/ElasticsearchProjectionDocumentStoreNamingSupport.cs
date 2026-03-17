namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDocumentStoreNamingSupport
{
    internal static Uri ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
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

    internal static string BuildIndexName(string indexPrefix, string indexScope)
    {
        var prefix = NormalizeToken(indexPrefix);
        if (prefix.Length == 0)
            prefix = "aevatar";
        return $"{prefix}-{indexScope}";
    }

    internal static string NormalizeToken(string? token)
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

    internal static string TruncatePayload(string payload)
    {
        const int maxLength = 512;
        if (payload.Length <= maxLength)
            return payload;

        return payload[..maxLength] + "...(truncated)";
    }
}
