using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sisyphus.Application.Models;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class SessionEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        // Graph ID endpoint — returns the resolved chrono-graph UUIDs
        app.MapGet("/api/graph-id", (GraphIdProvider provider) =>
            provider.ReadGraphId is not null || provider.WriteGraphId is not null
                ? Results.Ok(new { readGraphId = provider.ReadGraphId, writeGraphId = provider.WriteGraphId })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
            .WithTags("Graph");

        // SSE research endpoint — injects graph IDs from config into the prompt
        app.MapPost("/api/research", HandleResearchChat)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithTags("Research");

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

    /// <summary>
    /// SSE endpoint that injects configured graph IDs into the prompt
    /// and streams workflow output frames.
    /// </summary>
    private static async Task HandleResearchChat(
        HttpContext http,
        ResearchChatInput input,
        GraphIdProvider graphIdProvider,
        IWorkflowRunCommandService workflowRunService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (graphIdProvider.ReadGraphId is null || graphIdProvider.WriteGraphId is null)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Inject graph IDs into the prompt
        var prompt = $"""
            Read Graph ID: {graphIdProvider.ReadGraphId}
            Write Graph ID: {graphIdProvider.WriteGraphId}

            {input.Prompt}
            """;

        var started = false;

        try
        {
            await workflowRunService.ExecuteAsync(
                new WorkflowChatRunRequest(prompt, input.Workflow, input.AgentId),
                async (frame, token) =>
                {
                    if (!started)
                    {
                        started = true;
                        http.Response.StatusCode = StatusCodes.Status200OK;
                        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                        http.Response.Headers.CacheControl = "no-store";
                        http.Response.Headers.Pragma = "no-cache";
                        http.Response.Headers["X-Accel-Buffering"] = "no";
                        await http.Response.StartAsync(token);
                    }

                    var payload = JsonSerializer.Serialize(frame, SseJsonOptions);
                    var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
                    await http.Response.Body.WriteAsync(bytes, token);
                    await http.Response.Body.FlushAsync(token);
                },
                onStartedAsync: (_, token) =>
                {
                    if (!started)
                    {
                        started = true;
                        http.Response.StatusCode = StatusCodes.Status200OK;
                        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                        http.Response.Headers.CacheControl = "no-store";
                        http.Response.Headers.Pragma = "no-cache";
                        http.Response.Headers["X-Accel-Buffering"] = "no";
                        return new ValueTask(http.Response.StartAsync(token));
                    }
                    return ValueTask.CompletedTask;
                },
                ct);
        }
        catch (OperationCanceledException)
        {
        }
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

public sealed record ResearchChatInput(string? Prompt, string? Workflow, string? AgentId);
