using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Sagas.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Host.Api.Endpoints;

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

        group.MapGet("/sagas/workflow/{correlationId}", GetWorkflowSaga)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/sagas/workflow", ListWorkflowSagas)
            .Produces(StatusCodes.Status200OK);
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

    internal static async Task<IResult> GetWorkflowSaga(
        string correlationId,
        IWorkflowExecutionSagaQueryService queryService,
        CancellationToken ct = default)
    {
        var saga = await queryService.GetAsync(correlationId, ct);
        return saga == null ? Results.NotFound() : Results.Ok(saga);
    }

    internal static async Task<IResult> ListWorkflowSagas(
        IWorkflowExecutionSagaQueryService queryService,
        int take = 50,
        CancellationToken ct = default)
    {
        var sagas = await queryService.ListAsync(take, ct);
        return Results.Ok(sagas);
    }
}
