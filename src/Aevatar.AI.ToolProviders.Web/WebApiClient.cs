using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>HTTP client for web search and fetch operations.</summary>
public sealed class WebApiClient : IWebApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly WebToolOptions _options;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public WebApiClient(
        WebToolOptions options,
        HttpClient? httpClient = null,
        ILogger<WebApiClient>? logger = null)
    {
        _options = options;
        // Refactor (iter10/cluster-019):
        // Old: singleton DI registration could self-create and pin a raw HttpClient.
        // New: DI registers this as an AddHttpClient<T> typed client; only manual construction owns this fallback.
        _http = httpClient ?? CreateDefaultHttpClient();
        _ownsHttpClient = httpClient is null;
        _logger = logger ?? NullLogger<WebApiClient>.Instance;
    }

    /// <summary>Perform a web search via NyxID proxy or direct API.</summary>
    public async Task<WebSearchResult> SearchAsync(
        string token, string query, int maxResults, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.NyxIdSearchSlug) &&
            !string.IsNullOrWhiteSpace(_options.NyxIdBaseUrl))
        {
            var path = $"/search?q={Uri.EscapeDataString(query)}&limit={maxResults}";
            var url = $"{_options.NyxIdBaseUrl.TrimEnd('/')}/api/v1/proxy/{Uri.EscapeDataString(_options.NyxIdSearchSlug)}{path}";
            return WebToolResultBoundaryJson.ParseSearchPayload(await GetAsync(token, url, ct));
        }

        if (!string.IsNullOrWhiteSpace(_options.SearchApiBaseUrl))
        {
            var url = $"{_options.SearchApiBaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&limit={maxResults}";
            return WebToolResultBoundaryJson.ParseSearchPayload(await GetAsync(token, url, ct));
        }

        return ErrorResult("search_backend_not_configured", "No search backend configured. Set NyxIdSearchSlug or SearchApiBaseUrl in WebToolOptions.");
    }

    /// <summary>Fetch a URL and return the response body as text.</summary>
    public async Task<WebFetchResult> FetchUrlAsync(string token, string url, CancellationToken ct)
    {
        try
        {
            var validation = await WebFetchUrlGuard.ValidateResolvedAsync(url, ct);
            if (!validation.IsAllowed)
                return ValidationFailure(url, validation.RejectionCode);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.FetchTimeoutSeconds));

            var originalUrl = validation.NormalizedUrl!;
            var currentUrl = originalUrl;
            for (var redirectCount = 0; redirectCount < 5; redirectCount++)
            {
                using var request = BuildFetchRequest(token, currentUrl);
                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
                var statusCode = (int)response.StatusCode;

                if (IsRedirect(statusCode) && response.Headers.Location != null)
                {
                    var redirectUri = ResolveRedirectUri(currentUrl, response.Headers.Location);
                    var redirectValidation = await WebFetchUrlGuard.ValidateResolvedAsync(
                        redirectUri.ToString(),
                        cts.Token);
                    if (!redirectValidation.IsAllowed)
                        return ValidationFailure(
                            originalUrl,
                            redirectValidation.RejectionCode,
                            statusCode);

                    if (!string.Equals(
                            new Uri(currentUrl).Host,
                            redirectUri.Host,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new WebFetchResult(statusCode, contentType, null, redirectUri.ToString(), originalUrl);
                    }

                    currentUrl = redirectValidation.NormalizedUrl!;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failure(
                        originalUrl,
                        $"WEB_FETCH_HTTP_{statusCode}",
                        "The web request failed.",
                        statusCode);
                }

                var body = await ReadLimitedAsync(response, cts.Token);
                return new WebFetchResult(statusCode, contentType, body, null, originalUrl);
            }

            return Failure(
                originalUrl,
                "WEB_FETCH_TRANSPORT_FAILURE",
                "The web request failed.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(url, "WEB_FETCH_TIMEOUT", "The web request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "WebFetch failed for {Url}", url);
            return ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError =>
                    Failure(url, "WEB_FETCH_DNS_FAILURE", "The web host could not be resolved."),
                HttpRequestError.SecureConnectionError =>
                    Failure(url, "WEB_FETCH_TLS_FAILURE", "The secure web connection failed."),
                _ => Failure(url, "WEB_FETCH_TRANSPORT_FAILURE", "The web request failed."),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebFetch failed for {Url}", url);
            return Failure(url, "WEB_FETCH_TRANSPORT_FAILURE", "The web request failed.");
        }
    }

    private static WebFetchResult Failure(
        string url,
        string code,
        string message,
        int statusCode = 0) =>
        new(statusCode, "error", null, null, url, new WebToolError(code, message));

    private static WebFetchResult ValidationFailure(
        string url,
        string? rejectionCode,
        int statusCode = 0) =>
        rejectionCode == "host_resolution_failed"
            ? Failure(url, "WEB_FETCH_DNS_FAILURE", "The web host could not be resolved.", statusCode)
            : Failure(url, "WEB_FETCH_URL_REJECTED", "The web URL was rejected.", statusCode);

    private static HttpClient CreateDefaultHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        });

    private static HttpRequestMessage BuildFetchRequest(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AevatarAgent/1.0");

        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    private static Uri ResolveRedirectUri(string currentUrl, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(new Uri(currentUrl), location);

    private static bool IsRedirect(int statusCode) =>
        statusCode is 301 or 302 or 303 or 307 or 308;

    private async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[_options.MaxFetchBodyBytes];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        if (totalRead >= _options.MaxFetchBodyBytes)
            text += "\n... [truncated: exceeded max body size]";
        return text;
    }

    private async Task<string> GetAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web API request failed: {Url}", url);
            return System.Text.Json.JsonSerializer.Serialize(new { error = "request_failed", message = ex.Message });
        }
    }

    private static WebSearchResult ErrorResult(string code, string message) =>
        new(Array.Empty<WebSearchResultItem>(), new WebToolError(code, message));

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
