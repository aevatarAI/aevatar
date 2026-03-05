using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Host.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/register-trial", async (
            TrialRegisterRequest request,
            IOptions<AppAuthOptions> options,
            [FromServices] IAuthAppService authService) =>
        {
            if (!options.Value.TrialAuthEnabled)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
                return Results.BadRequest(new { error = "Please enter a valid email address" });

            var result = await authService.RegisterTrialAsync(request.Email, options.Value.TrialTokenSecret);
            if (result.IsExisting)
                return Results.Ok(new { token = result.Token, trialId = result.TrialId, existing = true });

            return Results.Created($"/api/users/me", new { token = result.Token, trialId = result.TrialId });
        });
    }
}

public sealed record TrialRegisterRequest(string Email, string? TurnstileToken = null);
