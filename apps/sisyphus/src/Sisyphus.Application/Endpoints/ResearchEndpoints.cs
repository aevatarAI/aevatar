using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // Set SSE headers
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        var clientDisconnected = false;

        // Run loop with CancellationToken.None so client disconnect doesn't kill it
        try
        {
            await loopService.RunAsync(async (type, payload, token) =>
            {

                if (clientDisconnected) return;
                try
                {
                    var eventObj = new Dictionary<string, object> { ["type"] = type.ToString() };

                    var payloadJson = JsonSerializer.Serialize(payload, SseJsonOptions);
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
                catch (Exception)
                {
                    clientDisconnected = true;
                    logger.LogInformation("SSE client disconnected, research loop continues in background");
                }
            }, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // TOCTOU race: another request started the loop between our check and RunAsync's lock
            logger.LogWarning("Research loop start rejected — already running (race)");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Research loop ended via cancellation");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research loop failed");
            if (!clientDisconnected)
            {
                try
                {
                    var errorJson = JsonSerializer.Serialize(
                        new { type = "LOOP_ERROR", error = "Internal error" }, SseJsonOptions);
                    var errorBytes = Encoding.UTF8.GetBytes($"data: {errorJson}\n\n");
                    await http.Response.Body.WriteAsync(errorBytes, ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                catch { /* Response may already be closed */ }
            }
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
