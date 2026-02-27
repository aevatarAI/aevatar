using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/graph/snapshot", async (
            ChronoGraphProxyService proxy,
            CancellationToken ct) =>
        {
            var json = await proxy.GetSnapshotAsync(ct);
            return Results.Content(json, "application/json");
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
