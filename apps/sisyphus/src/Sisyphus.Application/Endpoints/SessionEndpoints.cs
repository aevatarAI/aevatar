using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sisyphus.Application.Models;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/sessions").WithTags("Sessions");

        group.MapPost("/", HandleCreateSession)
            .Produces<ResearchSession>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/", HandleListSessions)
            .Produces<IReadOnlyList<ResearchSession>>();

        group.MapGet("/{id:guid}", HandleGetSession)
            .Produces<ResearchSession>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", HandleDeleteSession)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/run", HandleRunSession)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> HandleCreateSession(
        CreateSessionRequest request,
        SessionLifecycleService lifecycle,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
            return Results.BadRequest(new { code = "INVALID_TOPIC", message = "Topic is required." });

        if (request.Topic.Length > 500)
            return Results.BadRequest(new { code = "TOPIC_TOO_LONG", message = "Topic must be 500 characters or less." });

        var maxRounds = Math.Clamp(request.MaxRounds ?? 20, 1, 100);
        var session = await lifecycle.CreateSessionAsync(
            request.Topic, maxRounds, ct);
        return Results.Created($"/api/v2/sessions/{session.Id}", session);
    }

    private static IResult HandleListSessions(SessionLifecycleService lifecycle) =>
        Results.Ok(lifecycle.ListSessions());

    private static IResult HandleGetSession(Guid id, SessionLifecycleService lifecycle)
    {
        var session = lifecycle.GetSession(id);
        return session is null ? Results.NotFound() : Results.Ok(session);
    }

    private static async Task<IResult> HandleDeleteSession(
        Guid id, SessionLifecycleService lifecycle, CancellationToken ct)
    {
        var deleted = await lifecycle.DeleteSessionAsync(id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static IResult HandleRunSession(
        Guid id,
        SessionLifecycleService lifecycle,
        WorkflowTriggerService trigger)
    {
        var session = lifecycle.GetSession(id);
        if (session is null) return Results.NotFound();
        if (!lifecycle.TryStartSession(id))
            return Results.Conflict(new { code = "ALREADY_RUNNING", message = "Session is already running." });

        _ = trigger.TriggerAsync(session, ct: CancellationToken.None);
        return Results.Accepted($"/api/v2/sessions/{id}", new { status = "running", session.Id });
    }
}

public sealed record CreateSessionRequest(string? Topic, int? MaxRounds);
