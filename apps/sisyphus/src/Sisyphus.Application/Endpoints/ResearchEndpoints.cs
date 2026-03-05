using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sisyphus.Application.Models.Research;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class ResearchEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapResearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/research").WithTags("Research V2");

        group.MapPost("/start", HandleStart)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/subscribe", HandleSubscribe)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/stop", HandleStop)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/status", HandleStatus)
            .Produces(StatusCodes.Status200OK);

        return app;
    }

    private static async Task HandleStart(
        HttpContext http,
        ResearchLoopService loopService,
        ILogger<ResearchLoopService> logger,
        CancellationToken ct)
    {
        if (loopService.IsRunning)
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsJsonAsync(
                new { code = "ALREADY_RUNNING", message = "Research loop is already running" }, ct);
            return;
        }

        // Subscribe before starting so we don't miss events
        var (subId, reader) = loopService.Subscribe();

        // Start the loop in the background (not tied to this HTTP connection)
        _ = Task.Run(async () =>
        {
            try
            {
                await loopService.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Research loop background task failed");
            }
        });

        // Stream events to the caller
        try
        {
            await WriteSseStream(http, reader, logger, ct);
        }
        finally
        {
            loopService.Unsubscribe(subId);
        }
    }

    private static async Task HandleSubscribe(
        HttpContext http,
        ResearchLoopService loopService,
        ILogger<ResearchLoopService> logger,
        CancellationToken ct)
    {
        if (!loopService.IsRunning)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(
                new { code = "NOT_RUNNING", message = "No research loop is running" }, ct);
            return;
        }

        var (subId, reader) = loopService.Subscribe();

        try
        {
            await WriteSseStream(http, reader, logger, ct);
        }
        finally
        {
            loopService.Unsubscribe(subId);
        }
    }

    /// <summary>
    /// Shared SSE writer that reads from a subscriber channel and writes to the HTTP response.
    /// </summary>
    private static async Task WriteSseStream(
        HttpContext http,
        ChannelReader<ResearchSseMessage> reader,
        ILogger logger,
        CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        try
        {
            await foreach (var msg in reader.ReadAllAsync(ct))
            {
                var eventObj = new Dictionary<string, object> { ["type"] = msg.Type.ToString() };

                var payloadJson = JsonSerializer.Serialize(msg.Payload, SseJsonOptions);
                var payloadDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);
                if (payloadDict is not null)
                {
                    foreach (var (key, value) in payloadDict)
                        eventObj[key] = value;
                }

                var json = JsonSerializer.Serialize(eventObj, SseJsonOptions);
                var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                await http.Response.Body.WriteAsync(bytes, ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE client disconnected");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SSE stream error");
        }
    }

    private static IResult HandleStop(ResearchLoopService loopService)
    {
        if (!loopService.IsRunning)
            return Results.NotFound(new { code = "NOT_RUNNING", message = "No research loop is running" });

        loopService.Stop();
        return Results.Ok(new { message = "Stop signal sent" });
    }

    private static IResult HandleStatus(ResearchLoopService loopService)
    {
        return Results.Ok(new
        {
            is_running = loopService.IsRunning,
            current_round = loopService.CurrentRound,
        });
    }
}
