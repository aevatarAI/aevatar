using Aevatar.Workflow.Application.Abstractions.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Host.Api.Endpoints;

public static class ChatQueryEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/runs", ListRuns)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/runs/{runId}", GetRun)
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

    internal static async Task<IResult> ListRuns(
        IWorkflowExecutionQueryApplicationService queryService,
        int take = 50,
        CancellationToken ct = default)
    {
        var runs = await queryService.ListRunsAsync(take, ct);
        return Results.Ok(runs);
    }

    internal static async Task<IResult> GetRun(
        string runId,
        IWorkflowExecutionQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var report = await queryService.GetRunAsync(runId, ct);
        return report == null ? Results.NotFound() : Results.Ok(report);
    }
}
