using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sisyphus.Application.Services;

public class ChronoGraphWriteService(
    HttpClient httpClient,
    IOptions<ChronoGraphOptions> options,
    NyxIdTokenService tokenService,
    ILogger<ChronoGraphWriteService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    /// <summary>
    /// Creates nodes in batch. Returns list of assigned UUIDs (parallel to input).
    /// Each item must have "type" and "properties" keys.
    /// </summary>
    public virtual async Task<List<string>> CreateNodesAsync(
        string graphId,
        List<Dictionary<string, object>> nodeItems,
        CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        var url = $"{baseUrl}/api/graphs/{graphId}/nodes";
        var body = new { nodes = nodeItems };

        logger.LogInformation("Creating {Count} nodes in graph {GraphId}", nodeItems.Count, graphId);

        var items = await PostWithRetryAsync<List<GraphItem>>(url, body, ct);
        var uuids = items?.Select(i => i.Id).ToList() ?? [];

        logger.LogInformation("Created {Count} nodes, received {UuidCount} UUIDs", nodeItems.Count, uuids.Count);
        return uuids;
    }

    /// <summary>
    /// Creates edges in batch. Returns list of assigned UUIDs (parallel to input).
    /// Each item must have "source", "target", "type", and optionally "properties" keys.
    /// </summary>
    public virtual async Task<List<string>> CreateEdgesAsync(
        string graphId,
        List<Dictionary<string, object>> edgeItems,
        CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        var url = $"{baseUrl}/api/graphs/{graphId}/edges";
        var body = new { edges = edgeItems };

        logger.LogInformation("Creating {Count} edges in graph {GraphId}", edgeItems.Count, graphId);

        var items = await PostWithRetryAsync<List<GraphItem>>(url, body, ct);
        var uuids = items?.Select(i => i.Id).ToList() ?? [];

        logger.LogInformation("Created {Count} edges, received {UuidCount} UUIDs", edgeItems.Count, uuids.Count);
        return uuids;
    }

    private async Task<T?> PostWithRetryAsync<T>(string url, object body, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var token = await tokenService.GetAccessTokenAsync(ct);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonContent.Create(body);

                var response = await httpClient.SendAsync(request, ct);

                if (IsTransientError(response.StatusCode) && attempt < MaxRetries)
                {
                    logger.LogWarning(
                        "Transient error {StatusCode} on attempt {Attempt}/{MaxRetries} for {Url}, retrying after {Delay}",
                        (int)response.StatusCode, attempt + 1, MaxRetries, url, RetryDelays[attempt]);
                    await Task.Delay(RetryDelays[attempt], ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                logger.LogWarning(
                    "HTTP exception on attempt {Attempt}/{MaxRetries} for {Url}, retrying after {Delay}",
                    attempt + 1, MaxRetries, url, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                logger.LogWarning(
                    "Request timeout on attempt {Attempt}/{MaxRetries} for {Url}, retrying after {Delay}",
                    attempt + 1, MaxRetries, url, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        // Should not reach here — last attempt's exception propagates above
        throw new HttpRequestException($"All {MaxRetries} retries exhausted for {url}");
    }

    private static bool IsTransientError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout;

    private string ResolveBaseUrl()
    {
        var baseUrl = options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Sisyphus:ChronoGraph:BaseUrl is not configured");
        return baseUrl.TrimEnd('/');
    }

    // Response model for chrono-graph API (both nodes and edges return the same shape)
    private sealed class GraphItem
    {
        public string Id { get; set; } = "";
    }
}
