using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Endpoints;

public static class UploadEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/sessions/upload/start", HandleUploadStart)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .DisableAntiforgery()
            .WithTags("Upload");

        return app;
    }

    private static async Task HandleUploadStart(
        HttpContext http,
        IFormFile file,
        UploadPipelineService pipeline,
        ILogger<UploadPipelineService> logger,
        CancellationToken ct)
    {
        logger.LogInformation("Upload request received: {FileName}, {Size} bytes", file.FileName, file.Length);

        // Set SSE headers immediately
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        try
        {
            // Copy file to memory so the pipeline outlives the HTTP request
            using var memStream = new MemoryStream();
            await using (var fileStream = file.OpenReadStream())
            {
                await fileStream.CopyToAsync(memStream, ct);
            }
            memStream.Position = 0;

            // Pipeline runs with CancellationToken.None so client disconnect won't kill it.
            // SSE writes silently swallow errors if the client is gone.
            var clientDisconnected = false;
            await pipeline.ExecuteAsync(memStream, async (type, payload, token) =>
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
                    // Client disconnected — stop writing but let pipeline continue
                    clientDisconnected = true;
                    logger.LogInformation("SSE client disconnected, pipeline continues in background");
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Upload file read cancelled by client before pipeline started");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload pipeline failed");

            try
            {
                var errorJson = JsonSerializer.Serialize(
                    new { type = "ERROR", message = "Internal pipeline error" }, SseJsonOptions);
                var errorBytes = Encoding.UTF8.GetBytes($"data: {errorJson}\n\n");
                await http.Response.Body.WriteAsync(errorBytes, ct);
                await http.Response.Body.FlushAsync(ct);
            }
            catch
            {
                // Response may already be closed
            }
        }
    }
}
