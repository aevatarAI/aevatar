using Aevatar.App.Application.Concurrency;
using Aevatar.App.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.App.Host.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            [FromServices] IImageConcurrencyCoordinator coordinator,
            [FromServices] IImageStorageAppService storageService) =>
        {
            string grainState = "ok";
            string storage = storageService.IsConfigured() ? "ok" : "error";
            int activeGenerates = 0;
            int activeUploads = 0;
            int availableSlots = 0;
            int queueLength = 0;
            int maxTotal = 0;

            try
            {
                var stats = await coordinator.GetStatsAsync();
                activeGenerates = stats.ActiveGenerates;
                activeUploads = stats.ActiveUploads;
                availableSlots = stats.AvailableSlots;
                queueLength = stats.QueueLength;
                maxTotal = stats.MaxTotal;
                if (maxTotal <= 0)
                    grainState = "error";
            }
            catch
            {
                grainState = "error";
            }

            var degraded = grainState != "ok" || storage != "ok";
            var payload = new
            {
                status = degraded ? "degraded" : "ok",
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                checks = new { grain_state = grainState, storage },
                concurrency = new
                {
                    activeGenerates,
                    activeUploads,
                    availableSlots,
                    queueLength,
                    maxTotal
                }
            };

            return Results.Json(
                payload,
                statusCode: degraded ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        });

        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        }));

        app.MapGet("/health/ready", async ([FromServices] IImageConcurrencyCoordinator coordinator) =>
        {
            try
            {
                var ready = (await coordinator.GetStatsAsync()).MaxTotal > 0;
                return Results.Json(
                    new
                    {
                        status = ready ? "ready" : "not_ready",
                        timestamp = DateTimeOffset.UtcNow.ToString("O")
                    },
                    statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            }
            catch
            {
                return Results.Json(
                    new
                    {
                        status = "not_ready",
                        timestamp = DateTimeOffset.UtcNow.ToString("O")
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/api/info", () => Results.Ok(new
        {
            status = "ok",
            service = "aevatar-app-api",
            version = "3.0.0",
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            endpoints = new
            {
                health = "/health",
                remoteConfig = "/api/remote-config",
                state = "GET /api/state",
                sync = "POST /api/sync",
                users = "/api/users/*",
                auth = "/api/auth/*",
                generate = "/api/generate/*",
                upload = "/api/upload/*"
            }
        }));
    }
}
