using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models;
using Sisyphus.Application.Models.Graph;

namespace Sisyphus.Application.Services;

public class ChronoGraphReadService(
    HttpClient httpClient,
    IOptions<ChronoGraphOptions> options,
    GraphIdProvider graphIdProvider,
    NyxIdTokenService tokenService,
    ILogger<ChronoGraphReadService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Gets the full graph snapshot from chrono-graph.
    /// </summary>
    public virtual async Task<GraphSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var (baseUrl, graphId) = ResolveConfig();
        var url = $"{baseUrl}/api/graphs/{graphId}/snapshot";

        logger.LogInformation("Reading graph snapshot from {Url}", url);

        var token = await tokenService.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var snapshot = await JsonSerializer.DeserializeAsync<GraphSnapshot>(stream, JsonOptions, ct)
            ?? new GraphSnapshot();

        logger.LogInformation("Graph snapshot loaded: {NodeCount} nodes, {EdgeCount} edges",
            snapshot.Nodes.Count, snapshot.Edges.Count);

        return snapshot;
    }

    /// <summary>
    /// Gets only purified (blue) nodes and edges from the graph snapshot.
    /// </summary>
    public virtual async Task<BlueGraphSnapshot> GetBlueSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);

        var blueNodes = snapshot.Nodes
            .Where(n => HasSisyphusStatus(n.Properties, SisyphusStatus.Purified))
            .ToList();

        var blueNodeIds = new HashSet<string>(blueNodes.Select(n => n.Id));

        var blueEdges = snapshot.Edges
            .Where(e => HasSisyphusStatus(e.Properties, SisyphusStatus.Purified)
                        && blueNodeIds.Contains(e.Source)
                        && blueNodeIds.Contains(e.Target))
            .ToList();

        logger.LogInformation("Blue snapshot filtered: {NodeCount} nodes, {EdgeCount} edges",
            blueNodes.Count, blueEdges.Count);

        return new BlueGraphSnapshot
        {
            Nodes = blueNodes,
            Edges = blueEdges,
        };
    }

    private static bool HasSisyphusStatus(Dictionary<string, JsonElement> properties, string status)
    {
        if (!properties.TryGetValue(SisyphusStatus.PropertyName, out var element))
            return false;

        return element.ValueKind == JsonValueKind.String && element.GetString() == status;
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
