using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class ConnectedServiceSpecCache : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly NyxIdToolOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ConnectedServiceSpecCache(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<ConnectedServiceSpecCache>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<ConnectedServiceSpecCache>.Instance;
    }

    public async Task<OperationCard[]?> GetOrFetchAsync(
        string slug,
        string? specUrl,
        string accessToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var url = ResolveSpecUrl(slug, specUrl);
        if (url is null)
            return null;

        var cacheKey = $"{slug}|{url}";
        if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
            return entry.Operations;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Only attach bearer token to URLs on the NyxID host to prevent token leakage
            if (IsTrustedHost(url))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ConnectedServiceSpecCache: spec fetch for '{Slug}' returned {Status}",
                    slug, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var operations = OpenApiSpecParser.ParseSpec(json, slug);

            _cache[cacheKey] = new CacheEntry(operations, DateTime.UtcNow + CacheTtl);
            _logger.LogInformation(
                "ConnectedServiceSpecCache: cached {Count} operations for '{Slug}'",
                operations.Length, slug);

            return operations;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("ConnectedServiceSpecCache: spec fetch for '{Slug}' timed out", slug);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ConnectedServiceSpecCache: spec fetch for '{Slug}' failed", slug);
            return null;
        }
    }

    internal bool IsTrustedHost(string url)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return false;

        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/'));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
            return false;

        return string.Equals(baseUri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase)
               && baseUri.Port == targetUri.Port
               && string.Equals(baseUri.Scheme, targetUri.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveSpecUrl(string slug, string? specUrl)
    {
        if (!string.IsNullOrWhiteSpace(specUrl))
            return specUrl;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return null;

        return $"{_options.BaseUrl.TrimEnd('/')}/api/v1/proxy/services/{Uri.EscapeDataString(slug)}/openapi.json";
    }

    public void Dispose() => _http.Dispose();

    private sealed record CacheEntry(OperationCard[] Operations, DateTime ExpiresAt)
    {
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    }
}
