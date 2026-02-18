using Aevatar.Maker.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Maker.Host.Api.Endpoints;

public sealed record MakerRunInput
{
    public required string WorkflowYaml { get; init; }
    public required string WorkflowName { get; init; }
    public required string Input { get; init; }
    public string? ActorId { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool DestroyActorAfterRun { get; init; }
}

public static class MakerEndpoints
{
    public static IEndpointRouteBuilder MapMakerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/maker").WithTags("Maker");

        group.MapPost("/runs", ExecuteRun)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> ExecuteRun(
        MakerRunInput input,
        IMakerRunApplicationService service,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.WorkflowYaml) ||
            string.IsNullOrWhiteSpace(input.WorkflowName) ||
            string.IsNullOrWhiteSpace(input.Input))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_REQUEST",
                message = "workflowYaml, workflowName and input are required.",
            });
        }

        var request = new MakerRunRequest(
            input.WorkflowYaml,
            input.WorkflowName,
            input.Input,
            input.ActorId,
            input.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(input.TimeoutSeconds.Value) : null,
            input.DestroyActorAfterRun);

        var result = await service.ExecuteAsync(request, ct);
        return Results.Ok(new
        {
            actorId = result.Started.ActorId,
            workflowName = result.Started.WorkflowName,
            startedAt = result.Started.StartedAt,
            output = result.Output,
            success = result.Success,
            timedOut = result.TimedOut,
            error = result.Error,
        });
    }
}
