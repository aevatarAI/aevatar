using Aevatar.Foundation.Abstractions;
using Aevatar.Host.Api.Workflows;
using Aevatar.Workflow.Projection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Host.Api.Endpoints;

public static class ChatQueryEndpoints
{
    public static void Map(
        RouteGroupBuilder group,
        IWorkflowExecutionProjectionService projectionService)
    {
        group.MapGet("/agents", ListAgents)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/workflows", ListWorkflows)
            .Produces(StatusCodes.Status200OK);

        if (!projectionService.EnableRunQueryEndpoints)
            return;

        group.MapGet("/runs", ListRuns)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/runs/{runId}", GetRun)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAgents(IActorRuntime runtime)
    {
        var actors = await runtime.GetAllAsync();
        var result = new List<object>();
        foreach (var actor in actors)
        {
            var desc = await actor.Agent.GetDescriptionAsync();
            result.Add(new
            {
                id = actor.Id,
                type = actor.Agent.GetType().Name,
                description = desc,
            });
        }
        return Results.Ok(result);
    }

    private static IResult ListWorkflows(WorkflowRegistry registry) =>
        Results.Ok(registry.GetNames());

    private static async Task<IResult> ListRuns(
        IWorkflowExecutionProjectionService projectionService,
        int take = 50,
        CancellationToken ct = default)
    {
        var reports = await projectionService.ListRunsAsync(take, ct);
        var items = reports.Select(r => new
        {
            r.RunId,
            r.WorkflowName,
            r.RootActorId,
            r.StartedAt,
            r.EndedAt,
            r.DurationMs,
            r.Success,
            totalSteps = r.Summary.TotalSteps,
        });
        return Results.Ok(items);
    }

    private static async Task<IResult> GetRun(
        string runId,
        IWorkflowExecutionProjectionService projectionService,
        CancellationToken ct = default)
    {
        var report = await projectionService.GetRunAsync(runId, ct);
        return report == null ? Results.NotFound() : Results.Ok(report);
    }
}
