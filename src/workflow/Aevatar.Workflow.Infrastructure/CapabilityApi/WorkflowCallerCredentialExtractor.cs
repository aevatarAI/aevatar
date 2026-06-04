using Microsoft.AspNetCore.Http;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";

    public static WorkflowCallerCredential? Extract(HttpContext? http)
    {
        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null || !auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var bearerToken = auth[BearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(bearerToken)
            ? null
            : new WorkflowCallerCredential(bearerToken);
    }
}
