using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public sealed class EndpointAuditFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var metadata = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EndpointAuditMetadata>();
        if (metadata is null)
        {
            return await next(context);
        }

        var arguments = context.Arguments.ToArray();
        await CaptureRequestBestEffortAsync(context.HttpContext, metadata, arguments);

        try
        {
            var result = await next(context);
            await CaptureResultBestEffortAsync(context.HttpContext, metadata, arguments, result);
            return result;
        }
        catch (Exception ex)
        {
            EndpointAuditHttpContextState.CaptureException(context.HttpContext, ex);
            throw;
        }
    }

    private static async ValueTask CaptureRequestBestEffortAsync(
        HttpContext httpContext,
        EndpointAuditMetadata metadata,
        IReadOnlyList<object?> arguments)
    {
        try
        {
            await EndpointAuditHttpContextState.CaptureRequestAsync(httpContext, metadata, arguments);
        }
        catch
        {
            EndpointAuditHttpContextState.CaptureSummaryFailure(httpContext);
        }
    }

    private static async ValueTask CaptureResultBestEffortAsync(
        HttpContext httpContext,
        EndpointAuditMetadata metadata,
        IReadOnlyList<object?> arguments,
        object? result)
    {
        try
        {
            await EndpointAuditHttpContextState.CaptureResultAsync(httpContext, metadata, arguments, result);
        }
        catch
        {
            EndpointAuditHttpContextState.CaptureSummaryFailure(httpContext);
        }
    }
}
