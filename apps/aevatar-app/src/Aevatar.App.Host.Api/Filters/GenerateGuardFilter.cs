using Aevatar.App.Application.Concurrency;

namespace Aevatar.App.Host.Api.Filters;

public sealed class GenerateGuardFilter : IEndpointFilter
{
    private readonly IImageConcurrencyCoordinator _coordinator;

    public GenerateGuardFilter(IImageConcurrencyCoordinator coordinator) => _coordinator = coordinator;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var acquire = await _coordinator.TryAcquireGenerateAsync(ctx.HttpContext.RequestAborted);
        if (!acquire.Acquired)
        {
            var reason = acquire.Reason == AcquireFailureReason.RateLimit ? "rate_limit" : "overloaded";
            var statusCode = acquire.Reason == AcquireFailureReason.RateLimit
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status503ServiceUnavailable;

            return Results.Json(
                new
                {
                    success = false,
                    reason,
                    message = acquire.Message
                },
                statusCode: statusCode);
        }

        try
        {
            return await next(ctx);
        }
        finally
        {
            await _coordinator.ReleaseGenerateAsync();
        }
    }
}
