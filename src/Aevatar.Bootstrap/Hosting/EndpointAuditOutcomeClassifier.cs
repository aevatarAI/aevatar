using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Bootstrap.Hosting;

public static class EndpointAuditOutcomeClassifier
{
    public static AuditOutcome Classify(HttpContext context, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (exception is OperationCanceledException)
        {
            return AuditOutcome.Cancelled;
        }

        if (exception is not null)
        {
            return AuditOutcome.Error;
        }

        if (EndpointAuditHttpContextState.TryGetException(context, out var capturedException))
        {
            return capturedException is OperationCanceledException
                ? AuditOutcome.Cancelled
                : AuditOutcome.Error;
        }

        return context.Response.StatusCode switch
        {
            StatusCodes.Status403Forbidden => AuditOutcome.Denied,
            StatusCodes.Status401Unauthorized => AuditOutcome.Denied,
            >= 500 => AuditOutcome.Error,
            >= 400 => AuditOutcome.Error,
            >= 200 and <= 299 => AuditOutcome.Accepted,
            _ => AuditOutcome.Success,
        };
    }
}
