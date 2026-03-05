using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Services;
using Aevatar.App.Host.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.App.Host.Api.Endpoints;

public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/upload/plant-image", UploadPlantImage)
            .AddEndpointFilter<UploadTrackerFilter>();
    }

    private static async Task<IResult> UploadPlantImage(
        HttpContext ctx,
        UploadPlantImageRequest request,
        [FromServices] IAppAuthContextAccessor authAccessor,
        [FromServices] IImageStorageAppService storage)
    {
        if (request.Stage is not ("seed" or "sprout" or "growing" or "blooming"))
            return Results.BadRequest(new { error = "stage must be seed|sprout|growing|blooming" });
        if (string.IsNullOrWhiteSpace(request.ImageData))
            return Results.BadRequest(new { error = "imageData is required" });

        var auth = authAccessor.RequireAuthContext();
        var manifestationId = $"upload_{Guid.NewGuid():N}";

        try
        {
            var result = await storage.UploadAsync(
                auth.UserId, manifestationId, request.Stage, request.ImageData, ctx.RequestAborted);
            return Results.Ok(new { success = true, imageUrl = result.Url });
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { error = "Invalid base64 image data" });
        }
        catch (Exception ex)
        {
            return Results.Json(
                new { success = false, error = "Upload failed", message = ex.Message },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

public sealed record UploadPlantImageRequest(string Stage, string ImageData);
