using Aevatar.App.Application.Concurrency;

namespace Aevatar.App.Host.Api.Filters;

public sealed class UploadTrackerFilter : IEndpointFilter
{
    private readonly IImageConcurrencyCoordinator _coordinator;

    public UploadTrackerFilter(IImageConcurrencyCoordinator coordinator) => _coordinator = coordinator;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var acquire = await _coordinator.TryAcquireUploadAsync(ctx.HttpContext.RequestAborted);
        if (!acquire.Acquired)
        {
            return Results.Json(
                new { error = "image-upload", message = acquire.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return await next(ctx);
        }
        finally
        {
            await _coordinator.ReleaseUploadAsync();
        }
    }
}
