using Aevatar.CQRS.Core.Abstractions.Queries;
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
        IAgentQueryService<WorkflowAgentSummary> queryService,
        CancellationToken ct = default)
    {
        var agents = await queryService.ListAgentsAsync(ct);
        return Results.Ok(agents);
    }

    internal static IResult ListWorkflows(IExecutionTemplateQueryService queryService) =>
        Results.Ok(queryService.ListTemplates());

    internal static async Task<IResult> ListRuns(
        IExecutionQueryService<WorkflowRunSummary, WorkflowRunReport> queryService,
        int take = 50,
        CancellationToken ct = default)
    {
        var runs = await queryService.ListAsync(take, ct);
        return Results.Ok(runs);
    }

    internal static async Task<IResult> GetRun(
        string runId,
        IExecutionQueryService<WorkflowRunSummary, WorkflowRunReport> queryService,
        CancellationToken ct = default)
    {
        var report = await queryService.GetAsync(runId, ct);
        return report == null ? Results.NotFound() : Results.Ok(report);
    }
}
