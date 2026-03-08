using Aevatar.App.Application.Services;
using Microsoft.AspNetCore.Http;

namespace Aevatar.App.Host.Api.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/revenuecat", async (
            HttpContext httpContext,
            RevenueCatWebhookPayload payload,
            IRevenueCatWebhookHandler handler,
            IConfiguration configuration) =>
        {
            var expectedSecret = configuration["RevenueCat:WebhookSecret"];
            if (!string.IsNullOrWhiteSpace(expectedSecret))
            {
                var authHeader = httpContext.Request.Headers.Authorization.ToString();
                var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? authHeader["Bearer ".Length..].Trim()
                    : "";

                if (!string.Equals(token, expectedSecret, StringComparison.Ordinal))
                    return Results.Unauthorized();
            }

            await handler.HandleAsync(payload);
            return Results.Ok();
        });
    }
}
