using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class ConnectedServiceSpecCache : IConnectedServiceSpecSource, IDisposable
{
    public const string HttpClientName = "nyxid-connected-service-spec-cache";

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _manualHttpClient;
    private readonly NyxIdToolOptions _options;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    // Internal manual ctor avoids DI ambiguity while remaining callable from tests via
    // InternalsVisibleTo("Aevatar.AI.Tests").
    internal ConnectedServiceSpecCache(
        NyxIdToolOptions options,
        HttpClient? httpClient = null,
        ILogger<ConnectedServiceSpecCache>? logger = null)
    {
        _options = options;
        // Refactor (iter10/cluster-019):
        // Old: injected and self-created HttpClient ownership was ambiguous and disposed unconditionally.
        // New: DI uses IHttpClientFactory named clients; manual construction owns only its fallback client.
        _manualHttpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _logger = logger ?? NullLogger<ConnectedServiceSpecCache>.Instance;
    }

    public ConnectedServiceSpecCache(
        NyxIdToolOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<ConnectedServiceSpecCache>? logger = null)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        // Refactor (iter10/cluster-019):
        // Old: singleton cache pinned one raw HttpClient for the process lifetime.
        // New: spec fetches use named clients from IHttpClientFactory without keeping process-local spec state.
        _logger = logger ?? NullLogger<ConnectedServiceSpecCache>.Instance;
    }

    public async Task<OperationCard[]?> GetOrFetchAsync(
        string slug,
        string? serviceId,
        string? specUrl,
        string accessToken,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源; connected-service spec hints fetch live per request and never keep OpenAPI facts in process-local snapshots.
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var url = ResolveSpecUrl(serviceId, specUrl);
        if (url is null)
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FetchTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Only attach bearer token to URLs on the NyxID host to prevent token leakage
            if (IsTrustedHost(url))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var (http, disposeHttpClient) = CreateHttpClient();
            try
            {
                using var response = await http.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "ConnectedServiceSpecCache: spec fetch for '{Slug}' returned {Status}",
                        slug, (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var operations = OpenApiSpecParser.ParseSpec(json, slug);

                _logger.LogInformation(
                    "ConnectedServiceSpecCache: fetched {Count} operations for '{Slug}'",
                    operations.Length, slug);

                return operations;
            }
            finally
            {
                if (disposeHttpClient)
                    http.Dispose();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("ConnectedServiceSpecCache: spec fetch for '{Slug}' timed out", slug);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConnectedServiceSpecCache: spec fetch for '{Slug}' failed", slug);
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

    private string? ResolveSpecUrl(string? serviceId, string? specUrl)
    {
        if (!string.IsNullOrWhiteSpace(specUrl))
            return specUrl;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(serviceId))
            return null;

        return $"{_options.BaseUrl.TrimEnd('/')}/api/v1/proxy/services/{Uri.EscapeDataString(serviceId)}/openapi.json";
    }

    private (HttpClient Client, bool DisposeClient) CreateHttpClient()
    {
        if (_httpClientFactory is not null)
            return (_httpClientFactory.CreateClient(HttpClientName), true);

        return (_manualHttpClient ?? throw new ObjectDisposedException(nameof(ConnectedServiceSpecCache)), false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        if (_ownsHttpClient)
            _manualHttpClient?.Dispose();
    }
}
