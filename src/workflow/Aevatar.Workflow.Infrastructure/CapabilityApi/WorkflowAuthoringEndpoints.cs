using Aevatar.Workflow.Application.Abstractions.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowAuthoringEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        MapParse(group, "/workflow-authoring/parse");
        MapParse(group, "/playground/parse");
        MapSave(group, "/workflow-authoring/workflows");
        MapSave(group, "/playground/workflows");

        group.MapGet("/primitives", ListPrimitives)
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/llm/status", GetLlmStatus)
            .Produces(StatusCodes.Status200OK);
    }

    private static void MapParse(RouteGroupBuilder group, string pattern)
    {
        group.MapPost(pattern, ParseWorkflow)
            .Accepts<PlaygroundWorkflowParseRequest>("application/json")
            .Produces<PlaygroundWorkflowParseResult>(StatusCodes.Status200OK)
            .Produces<PlaygroundWorkflowParseResult>(StatusCodes.Status400BadRequest);
    }

    private static void MapSave(RouteGroupBuilder group, string pattern)
    {
        group.MapPost(pattern, SaveWorkflow)
            .Accepts<PlaygroundWorkflowSaveRequest>("application/json")
            .Produces<PlaygroundWorkflowSaveResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
    }

    internal static async Task<IResult> ParseWorkflow(
        [FromBody] PlaygroundWorkflowParseRequest request,
        [FromServices] IWorkflowAuthoringQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(queryService);

        var result = await queryService.ParseWorkflowAsync(request, ct);
        return result.Valid ? Results.Ok(result) : Results.BadRequest(result);
    }

    internal static async Task<IResult> SaveWorkflow(
        [FromBody] PlaygroundWorkflowSaveRequest request,
        [FromServices] IWorkflowAuthoringCommandApplicationService commandService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandService);

        try
        {
            var result = await commandService.SaveWorkflowAsync(request, ct);
            return Results.Ok(result);
        }
        catch (WorkflowAuthoringConflictException ex)
        {
            return Results.Conflict(new
            {
                error = ex.Message,
                filename = ex.Filename,
                path = ex.SavedPath,
            });
        }
        catch (WorkflowAuthoringValidationException ex)
        {
            return Results.BadRequest(new
            {
                error = ex.Message,
                errors = ex.Errors,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                error = ex.Message,
            });
        }
    }

    internal static async Task<IResult> ListPrimitives(
        [FromServices] IWorkflowAuthoringQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryService);
        var primitives = await queryService.ListPrimitivesAsync(ct);
        return Results.Ok(primitives);
    }

    internal static async Task<IResult> GetLlmStatus(
        [FromServices] IWorkflowAuthoringQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryService);
        var status = await queryService.GetLlmStatusAsync(ct);
        return Results.Ok(status);
    }
}
