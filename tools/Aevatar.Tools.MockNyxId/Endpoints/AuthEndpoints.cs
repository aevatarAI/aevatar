using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.MockNyxId.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/users/me", HandleGetCurrentUser).WithTags("Auth");
        app.MapPost("/api/v1/auth/test-token", HandleGenerateTestToken).WithTags("Auth");
        return app;
    }

    private static IResult HandleGetCurrentUser(
        HttpContext http,
        [FromServices] MockNyxIdOptions options)
    {
        var token = ExtractBearer(http);
        if (token is null)
            return Results.Json(new { error = true, message = "Missing Authorization header" }, statusCode: 401);

        var userId = MockJwtHelper.TryExtractSubject(token) ?? options.DefaultUserId;

        return Results.Json(new
        {
            id = userId,
            email = options.DefaultUserEmail,
            name = options.DefaultUserName,
            created_at = "2026-01-01T00:00:00Z",
            is_admin = false,
        });
    }

    private static IResult HandleGenerateTestToken(
        HttpContext http,
        [FromServices] MockJwtHelper jwtHelper,
        [FromServices] MockNyxIdOptions options)
    {
        // Accept optional JSON body: { "user_id": "...", "scope": "..." }
        string userId = options.DefaultUserId;
        string? scope = null;

        if (http.Request.ContentLength > 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(http.Request.Body);
                if (doc.RootElement.TryGetProperty("user_id", out var uid))
                    userId = uid.GetString() ?? userId;
                if (doc.RootElement.TryGetProperty("scope", out var s))
                    scope = s.GetString();
            }
            catch { /* ignore parse errors, use defaults */ }
        }

        var token = jwtHelper.GenerateToken(userId, scope);
        return Results.Json(new { token, user_id = userId });
    }

    internal static string? ExtractBearer(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth is null) return null;
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return auth;
    }
}
