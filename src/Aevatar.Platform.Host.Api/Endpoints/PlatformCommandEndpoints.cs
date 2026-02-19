using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Queries;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

namespace Aevatar.Platform.Host.Api.Endpoints;

public sealed record PlatformCommandInput
{
    public required string Subsystem { get; init; }
    public required string Command { get; init; }
    public string Method { get; init; } = "POST";
    public JsonElement? Payload { get; init; }
    public string ContentType { get; init; } = "application/json";
}

public static class PlatformCommandEndpoints
{
    public static IEndpointRouteBuilder MapPlatformCommandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("PlatformCQRS");

        group.MapPost("/commands", EnqueueCommand)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/commands/{commandId}", GetCommandStatus)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/commands", ListCommandStatuses)
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    private static async Task<IResult> EnqueueCommand(
        PlatformCommandInput input,
        IPlatformCommandApplicationService commandService,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Subsystem) ||
            string.IsNullOrWhiteSpace(input.Command) ||
            string.IsNullOrWhiteSpace(input.Method))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_REQUEST",
                message = "subsystem, command and method are required.",
            });
        }

        var payloadJson = input.Payload?.GetRawText();
        var result = await commandService.EnqueueAsync(
            new PlatformCommandRequest(
                input.Subsystem,
                input.Command,
                input.Method,
                payloadJson,
                input.ContentType),
            ct);

        if (result.Error == PlatformCommandStartError.InvalidRequest)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_REQUEST",
                message = "Invalid platform command request.",
            });
        }

        if (result.Error == PlatformCommandStartError.SubsystemNotFound)
        {
            return Results.NotFound(new
            {
                code = "SUBSYSTEM_NOT_FOUND",
                message = "Subsystem route is not configured.",
            });
        }

        if (result.Error == PlatformCommandStartError.EnqueueFailed)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (result.Started == null)
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        return Results.Accepted(
            $"/api/commands/{result.Started.CommandId}",
            new
            {
                commandId = result.Started.CommandId,
                subsystem = result.Started.Subsystem,
                command = result.Started.Command,
                method = result.Started.Method,
                targetEndpoint = result.Started.TargetEndpoint,
                acceptedAt = result.Started.AcceptedAt,
            });
    }

    private static async Task<IResult> GetCommandStatus(
        string commandId,
        IPlatformCommandQueryApplicationService queryService,
        CancellationToken ct = default)
    {
        var status = await queryService.GetByCommandIdAsync(commandId, ct);
        return status == null ? Results.NotFound() : Results.Ok(status);
    }

    private static async Task<IResult> ListCommandStatuses(
        IPlatformCommandQueryApplicationService queryService,
        int take = 50,
        CancellationToken ct = default)
    {
        var statuses = await queryService.ListAsync(take, ct);
        return Results.Ok(statuses);
    }

}
