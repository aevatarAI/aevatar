using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>HTTP client for calling Explorer REST API endpoints.</summary>
public sealed class ChronoStorageApiClient
{
    private readonly HttpClient _http;
    private readonly ChronoStorageToolOptions _options;
    private readonly ILogger _logger;

    public ChronoStorageApiClient(
        ChronoStorageToolOptions options,
        HttpClient? httpClient = null,
        ILogger<ChronoStorageApiClient>? logger = null)
    {
        _options = options;
        _http = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<ChronoStorageApiClient>.Instance;
    }

    /// <summary>GET /api/explorer/manifest — list all files.</summary>
    public Task<string> GetManifestAsync(string token, CancellationToken ct) =>
        GetAsync(token, "/api/explorer/manifest", ct);

    /// <summary>GET /api/explorer/grep — search file contents.</summary>
    public Task<string> GrepAsync(string token, string pattern, string? glob, int? maxResults, CancellationToken ct)
    {
        var query = $"pattern={Uri.EscapeDataString(pattern)}";
        if (!string.IsNullOrWhiteSpace(glob))
            query += $"&glob={Uri.EscapeDataString(glob)}";
        if (maxResults.HasValue)
            query += $"&maxResults={maxResults.Value}";
        return GetAsync(token, $"/api/explorer/grep?{query}", ct);
    }

    /// <summary>GET /api/explorer/files/{key} — read file content.</summary>
    public Task<string> GetFileAsync(string token, string key, CancellationToken ct) =>
        GetAsync(token, $"/api/explorer/files/{EncodeKeyPath(key)}", ct);

    /// <summary>PUT /api/explorer/files/{key} — write/create file.</summary>
    public Task<string> PutFileAsync(string token, string key, string content, CancellationToken ct) =>
        PutAsync(token, $"/api/explorer/files/{EncodeKeyPath(key)}", content, ct);

    /// <summary>DELETE /api/explorer/files/{key} — delete file.</summary>
    public Task<string> DeleteFileAsync(string token, string key, CancellationToken ct) =>
        DeleteAsync(token, $"/api/explorer/files/{EncodeKeyPath(key)}", ct);

    // ─── HTTP helpers ───

    private string GetBaseUrl() =>
        _options.ApiBaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("ChronoStorage API base URL is not configured.");

    private static string EncodeKeyPath(string key) =>
        string.Join("/", (key ?? string.Empty).Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));

    private async Task<string> GetAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<string> PutAsync(string token, string path, string body, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
        return await SendAsync(request, ct);
    }

    private async Task<string> DeleteAsync(string token, string path, CancellationToken ct)
    {
        var url = $"{GetBaseUrl()}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, ct);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ChronoStorage API request failed: {Method} {Url} -> {Status}",
                    request.Method, request.RequestUri, (int)response.StatusCode);
                return $"{{\"error\": true, \"status\": {(int)response.StatusCode}, \"body\": {System.Text.Json.JsonSerializer.Serialize(content)}}}";
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChronoStorage API request exception: {Method} {Url}", request.Method, request.RequestUri);
            return $"{{\"error\": true, \"message\": {System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}";
        }
    }
}
