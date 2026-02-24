using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ChatQueryEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}", GetActorSnapshot)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/actors/{actorId}/timeline", ListActorTimeline)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-edges", ListActorGraphEdges)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-subgraph", GetActorGraphSubgraph)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/actors/{actorId}/graph-enriched", GetActorGraphEnrichedSnapshot)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> ListAgents(
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var agents = await queryService.ListAgentsAsync(ct);
        return Results.Ok(agents);
    }

    internal static IResult ListWorkflows(IWorkflowExecutionQueryApplicationService queryService) =>
        Results.Ok(queryService.ListWorkflows());

    internal static async Task<IResult> GetActorSnapshot(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var snapshot = await queryService.GetActorSnapshotAsync(actorId, ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    internal static async Task<IResult> ListActorTimeline(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int take = 200,
        CancellationToken ct = default)
    {
        var timeline = await queryService.ListActorTimelineAsync(actorId, take, ct);
        return Results.Ok(timeline);
    }

    internal static async Task<IResult> ListActorGraphEdges(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var edges = await queryService.ListActorGraphEdgesAsync(actorId, take, graphOptions, ct);
        return Results.Ok(edges);
    }

    internal static async Task<IResult> GetActorGraphSubgraph(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var subgraph = await queryService.GetActorGraphSubgraphAsync(actorId, depth, take, graphOptions, ct);
        return Results.Ok(subgraph);
    }

    internal static async Task<IResult> GetActorGraphEnrichedSnapshot(
        string actorId,
        IWorkflowExecutionQueryApplicationService queryService,
        int depth = 2,
        int take = 200,
        string? direction = null,
        string[]? edgeTypes = null,
        CancellationToken ct = default)
    {
        var graphOptions = BuildGraphQueryOptions(direction, edgeTypes);
        var graphEnriched = await queryService.GetActorGraphEnrichedSnapshotAsync(actorId, depth, take, graphOptions, ct);
        return graphEnriched == null ? Results.NotFound() : Results.Ok(graphEnriched);
    }

    private static WorkflowActorGraphQueryOptions BuildGraphQueryOptions(
        string? direction,
        string[]? edgeTypes)
    {
        return new WorkflowActorGraphQueryOptions
        {
            Direction = ParseDirection(direction),
            EdgeTypes = NormalizeEdgeTypes(edgeTypes),
        };
    }

    private static WorkflowActorGraphDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return WorkflowActorGraphDirection.Both;

        return Enum.TryParse<WorkflowActorGraphDirection>(direction.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : WorkflowActorGraphDirection.Both;
    }

    private static IReadOnlyList<string> NormalizeEdgeTypes(IReadOnlyList<string>? edgeTypes)
    {
        if (edgeTypes == null || edgeTypes.Count == 0)
            return [];

        return edgeTypes
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

}
