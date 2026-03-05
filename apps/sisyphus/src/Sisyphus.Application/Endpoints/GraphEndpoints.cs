using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class GraphEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/graph/snapshot", async (
            ChronoGraphReadService readService,
            CancellationToken ct) =>
        {
            var snapshot = await readService.GetBlueSnapshotAsync(ct);
            // Map to the shape the frontend expects
            var result = new
            {
                nodes = snapshot.Nodes.Select(n => new
                {
                    n.Id,
                    n.Type,
                    n.Properties,
                }),
                edges = snapshot.Edges.Select(e => new
                {
                    e.Id,
                    sourceNodeId = e.Source,
                    targetNodeId = e.Target,
                    e.Type,
                }),
            };
            return Results.Json(result, JsonOptions);
        }).WithTags("Graph");

        app.MapGet("/api/graph/nodes/{nodeId}/traverse", async (
            string nodeId,
            int? depth,
            ChronoGraphProxyService proxy,
            CancellationToken ct) =>
        {
            var json = await proxy.TraverseNodeAsync(nodeId, depth ?? 2, ct);
            return Results.Content(json, "application/json");
        }).WithTags("Graph");

        return app;
    }
}
