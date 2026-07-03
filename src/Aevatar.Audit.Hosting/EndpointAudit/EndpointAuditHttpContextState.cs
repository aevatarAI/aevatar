using Microsoft.AspNetCore.Http;

namespace Aevatar.Audit.Hosting.EndpointAudit;

public static class EndpointAuditHttpContextState
{
    private static readonly object RequestSummaryKey = new();
    private static readonly object ResultSummaryKey = new();
    private static readonly object TargetKey = new();
    private static readonly object ExceptionKey = new();
    private static readonly object SummaryFailureKey = new();

    public static async ValueTask CaptureRequestAsync(
        HttpContext httpContext,
        EndpointAuditMetadata metadata,
        IReadOnlyList<object?>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(metadata);

        var target = await metadata.TargetResolver(httpContext);
        if (target is not null)
        {
            httpContext.Items[TargetKey] = target;
        }

        var summary = await metadata.RequestSanitizer(new EndpointAuditSanitizationContext(
            httpContext,
            metadata,
            arguments ?? [],
            Result: null));
        if (!string.IsNullOrWhiteSpace(summary))
        {
            httpContext.Items[RequestSummaryKey] = summary.Trim();
        }
    }

    public static async ValueTask CaptureResultAsync(
        HttpContext httpContext,
        EndpointAuditMetadata metadata,
        IReadOnlyList<object?>? arguments,
        object? result)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(metadata);

        var summary = await metadata.ResultSanitizer(new EndpointAuditSanitizationContext(
            httpContext,
            metadata,
            arguments ?? [],
            result));
        if (!string.IsNullOrWhiteSpace(summary))
        {
            httpContext.Items[ResultSummaryKey] = summary.Trim();
        }
    }

    public static void CaptureException(HttpContext httpContext, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        httpContext.Items[ExceptionKey] = exception;
    }

    public static void CaptureSummaryFailure(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Items[SummaryFailureKey] = true;
    }

    public static bool TryGetRequestSummary(HttpContext httpContext, out string summary)
    {
        return TryGetItem(httpContext, RequestSummaryKey, out summary);
    }

    public static bool TryGetResultSummary(HttpContext httpContext, out string summary)
    {
        return TryGetItem(httpContext, ResultSummaryKey, out summary);
    }

    public static bool TryGetTarget(HttpContext httpContext, out EndpointAuditTarget target)
    {
        if (httpContext.Items.TryGetValue(TargetKey, out var value) &&
            value is EndpointAuditTarget auditTarget)
        {
            target = auditTarget;
            return true;
        }

        target = default!;
        return false;
    }

    public static bool TryGetException(HttpContext httpContext, out Exception exception)
    {
        if (httpContext.Items.TryGetValue(ExceptionKey, out var value) &&
            value is Exception capturedException)
        {
            exception = capturedException;
            return true;
        }

        exception = default!;
        return false;
    }

    public static bool HasSummaryFailure(HttpContext httpContext)
    {
        return httpContext.Items.TryGetValue(SummaryFailureKey, out var value) &&
               value is true;
    }

    private static bool TryGetItem(HttpContext httpContext, object key, out string value)
    {
        if (httpContext.Items.TryGetValue(key, out var item) &&
            item is string text &&
            !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
