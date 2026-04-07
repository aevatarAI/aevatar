using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>HTTP client for web search and fetch operations.</summary>
public sealed class WebApiClient
{
    private readonly HttpClient _http;
    private readonly WebToolOptions _options;
    private readonly ILogger _logger;

    public WebApiClient(
        WebToolOptions options,
        HttpClient? httpClient = null,
        ILogger<WebApiClient>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<WebApiClient>.Instance;
    }

    /// <summary>Perform a web search via NyxID proxy or direct API.</summary>
    public async Task<string> SearchAsync(
        string token, string query, int maxResults, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.NyxIdSearchSlug) &&
            !string.IsNullOrWhiteSpace(_options.NyxIdBaseUrl))
        {
            var path = $"/search?q={Uri.EscapeDataString(query)}&limit={maxResults}";
            var url = $"{_options.NyxIdBaseUrl.TrimEnd('/')}/api/v1/proxy/{Uri.EscapeDataString(_options.NyxIdSearchSlug)}{path}";
            return await GetAsync(token, url, ct);
        }

        if (!string.IsNullOrWhiteSpace(_options.SearchApiBaseUrl))
        {
            var url = $"{_options.SearchApiBaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&limit={maxResults}";
            return await GetAsync(token, url, ct);
        }

        return """{"error":"No search backend configured. Set NyxIdSearchSlug or SearchApiBaseUrl in WebToolOptions."}""";
    }

    /// <summary>Fetch a URL and return the response body as text.</summary>
    public async Task<FetchResult> FetchUrlAsync(string token, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.FetchTimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("AevatarAgent/1.0");

            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await ReadLimitedAsync(response, cts.Token);
                return new FetchResult(statusCode, contentType, errorBody, null, url);
            }

            if (response.RequestMessage?.RequestUri != null &&
                !string.Equals(
                    new Uri(url).Host,
                    response.RequestMessage.RequestUri.Host,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new FetchResult(
                    statusCode, contentType, null,
                    response.RequestMessage.RequestUri.ToString(), url);
            }

            var body = await ReadLimitedAsync(response, cts.Token);
            return new FetchResult(statusCode, contentType, body, null, url);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult(0, "timeout", $"Request timed out after {_options.FetchTimeoutSeconds}s", null, url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebFetch failed for {Url}", url);
            return new FetchResult(0, "error", ex.Message, null, url);
        }
    }

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
            return System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}

/// <summary>Result of a URL fetch operation.</summary>
public sealed record FetchResult(
    int StatusCode,
    string ContentType,
    string? Body,
    string? RedirectUrl,
    string OriginalUrl);
