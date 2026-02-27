using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Sisyphus.Application.Services;

/// <summary>
/// Pass-through proxy for chrono-graph REST API calls.
/// </summary>
public sealed class ChronoGraphProxyService(
    HttpClient httpClient,
    IOptions<ChronoGraphOptions> options,
    GraphIdProvider graphIdProvider,
    NyxIdTokenService tokenService)
{
    /// <summary>
    /// Gets the full graph snapshot.
    /// </summary>
    public async Task<string> GetSnapshotAsync(CancellationToken ct = default)
    {
        var (baseUrl, graphId) = ResolveConfig();

        var token = await tokenService.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/graphs/{graphId}/snapshot");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Traverses a node in the graph to a given depth.
    /// </summary>
    public async Task<string> TraverseNodeAsync(string nodeId, int depth, CancellationToken ct = default)
    {
        var (baseUrl, graphId) = ResolveConfig();

        var token = await tokenService.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/graphs/{graphId}/nodes/{nodeId}/traverse?depth={depth}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private (string baseUrl, string graphId) ResolveConfig()
    {
        var baseUrl = options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Sisyphus:ChronoGraph:BaseUrl is not configured");
        var graphId = graphIdProvider.ReadGraphId;
        if (string.IsNullOrWhiteSpace(graphId))
            throw new InvalidOperationException("ReadGraphId is not available");
        return (baseUrl.TrimEnd('/'), graphId);
    }
}
