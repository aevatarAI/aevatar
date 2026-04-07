using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.MockNyxId.Endpoints;

public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/proxy/services", HandleDiscoverServices).WithTags("Proxy");

        // Catch-all proxy: any method, any slug, any path
        app.Map("/api/v1/proxy/s/{slug}/{**path}", HandleProxyCatchAll).WithTags("Proxy");

        return app;
    }

    private static IResult HandleDiscoverServices(
        HttpContext http,
        [FromServices] MockStore store)
    {
        if (AuthEndpoints.ExtractBearer(http) is null)
            return Results.Json(new { error = true, message = "Unauthorized" }, statusCode: 401);

        var services = store.Services.Values.ToList();
        return Results.Json(services);
    }

    private static async Task<IResult> HandleProxyCatchAll(
        HttpContext http,
        string slug,
        string? path,
        [FromServices] MockStore store)
    {
        if (AuthEndpoints.ExtractBearer(http) is null)
            return Results.Json(new { error = true, message = "Unauthorized" }, statusCode: 401);

        var method = http.Request.Method;
        var normalizedPath = path ?? string.Empty;

        // Read body if present
        string? body = null;
        if (http.Request.ContentLength > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var reader = new StreamReader(http.Request.Body);
            body = await reader.ReadToEndAsync();
        }

        // Log the request
        var log = store.ProxyLog.GetOrAdd(slug, _ => new());
        log.Add(new ProxyLogEntry(slug, normalizedPath, method, body, DateTimeOffset.UtcNow));

        // Platform-specific mock responses
        if (slug == "api-lark-bot" && normalizedPath.Contains("im/v1/messages"))
        {
            return Results.Json(new
            {
                code = 0,
                msg = "success",
                data = new
                {
                    message_id = $"mock-msg-{Guid.NewGuid():N}",
                    root_id = "",
                    parent_id = "",
                    msg_type = "text",
                    create_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                },
            });
        }

        if (slug == "api-telegram-bot" && normalizedPath.Contains("setWebhook"))
        {
            return Results.Json(new
            {
                ok = true,
                result = true,
                description = "Webhook was set",
            });
        }

        if (slug == "api-telegram-bot" && normalizedPath.Contains("sendMessage"))
        {
            return Results.Json(new
            {
                ok = true,
                result = new
                {
                    message_id = Random.Shared.Next(1, 999999),
                    date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    chat = new { id = 0, type = "private" },
                    text = "mock reply",
                },
            });
        }

        // Generic proxy response
        return Results.Json(new
        {
            ok = true,
            mock = true,
            slug,
            path = normalizedPath,
            method,
            body_received = body is not null,
            timestamp = DateTimeOffset.UtcNow,
        });
    }
}
