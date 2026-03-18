using Aevatar.App.Application.Services;
using Aevatar.App.Host.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.App.Host.Api.Endpoints;

public static class GenerateEndpoints
{
    public static void MapGenerateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/generate/manifestation", GenerateManifestation);
        app.MapPost("/api/generate/affirmation", GenerateAffirmation);
        app.MapPost("/api/generate/plant-image", GeneratePlantImage)
            .AddEndpointFilter<GenerateGuardFilter>();
        app.MapPost("/api/generate/speech", GenerateSpeech);
    }

    private static async Task<IResult> GenerateManifestation(
        HttpContext ctx,
        ManifestationRequest request,
        [FromServices] IGenerationAppService generationService)
    {
        if (string.IsNullOrWhiteSpace(request.UserGoal) || request.UserGoal.Length > 500)
            return Results.BadRequest(new { error = "userGoal must be 1-500 characters" });

        var result = await generationService.GenerateManifestationAsync(request.UserGoal, ctx.RequestAborted);
        return Results.Ok(new
        {
            mantra = result.Mantra,
            plantName = result.PlantName,
            plantDescription = result.PlantDescription
        });
    }

    private static async Task<IResult> GenerateAffirmation(
        HttpContext ctx,
        AffirmationRequest request,
        [FromServices] IGenerationAppService generationService)
    {
        var stage = request.Stage ?? "seed";
        var trigger = request.Trigger ?? "manual";

        if (string.IsNullOrWhiteSpace(request.UserGoal) || request.UserGoal.Length > 500)
            return Results.BadRequest(new { error = "userGoal must be 1-500 characters" });
        if (string.IsNullOrWhiteSpace(request.Mantra) || request.Mantra.Length > 500)
            return Results.BadRequest(new { error = "mantra must be 1-500 characters" });
        if (string.IsNullOrWhiteSpace(request.PlantName) || request.PlantName.Length > 200)
            return Results.BadRequest(new { error = "plantName must be 1-200 characters" });
        if (stage is not ("seed" or "sprout" or "growing" or "blooming"))
            return Results.BadRequest(new { error = "stage must be seed|sprout|growing|blooming" });
        if (trigger is not ("daily_interaction" or "evolution" or "watering" or "manual"))
            return Results.BadRequest(new { error = "trigger must be daily_interaction|evolution|watering|manual" });

        var result = await generationService.GenerateAffirmationAsync(
            request.UserGoal, request.Mantra, request.PlantName, ctx.RequestAborted);
        return Results.Ok(new
        {
            affirmation = result.Affirmation,
            trigger,
            stage,
            generatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private static async Task<IResult> GeneratePlantImage(
        HttpContext ctx,
        PlantImageRequest request,
        [FromServices] IAIGenerationAppService ai)
    {
        if (string.IsNullOrWhiteSpace(request.PlantName) || request.PlantName.Length > 200)
            return Results.BadRequest(new { error = "plantName must be 1-200 characters" });
        if (request.Stage is not ("seed" or "sprout" or "growing" or "blooming"))
            return Results.BadRequest(new { error = "stage must be seed|sprout|growing|blooming" });
        if (request.PlantDescription?.Length > 500)
            return Results.BadRequest(new { error = "plantDescription must be <= 500 characters" });

        var result = await ai.GenerateImageAsync(
            request.PlantName,
            request.PlantDescription ?? "",
            request.Stage,
            ct: ctx.RequestAborted,
            useInlineData: request.UseInlineData);

        if (!result.Succeeded)
        {
            var statusCode = result.Reason switch
            {
                "rate_limit" => 429,
                "overloaded" => 503,
                _ => 500
            };

            return Results.Json(
                new
                {
                    success = false,
                    reason = result.Reason ?? "unknown",
                    message = result.Message ?? "Image generation failed"
                },
                statusCode: statusCode);
        }

        return Results.Ok(new
        {
            success = true,
            imageData = result.ImageData,
            isPlaceholder = result.IsPlaceholder
        });
    }

    private static async Task<IResult> GenerateSpeech(
        HttpContext ctx,
        SpeechRequest request,
        [FromServices] IAIGenerationAppService ai)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1000)
            return Results.BadRequest(new { error = "text must be 1-1000 characters" });

        var result = await ai.GenerateSpeechAsync(request.Text, ctx.RequestAborted);

        if (!result.Succeeded)
        {
            var statusCode = result.Reason == "rate_limit" ? 429 : 500;
            return Results.Json(
                new { error = "Speech generation failed", reason = result.Reason },
                statusCode: statusCode);
        }

        return Results.Ok(new { audioData = result.AudioData });
    }

}

public sealed record ManifestationRequest(string UserGoal);
public sealed record AffirmationRequest(
    string UserGoal, string Mantra, string PlantName,
    string? ClientId = null, string? Stage = null, string? Trigger = null);
public sealed record PlantImageRequest(
    string ManifestationId,
    string PlantName,
    string Stage,
    string? PlantDescription = null,
    bool UseInlineData = false);
public sealed record SpeechRequest(string Text);
