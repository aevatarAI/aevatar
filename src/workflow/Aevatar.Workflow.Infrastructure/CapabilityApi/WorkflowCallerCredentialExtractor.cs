using Microsoft.AspNetCore.Http;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";

    public static WorkflowCallerCredential? Extract(HttpContext? http)
    {
        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null)
            return null;
        if (string.Equals(auth.Trim(), "Bearer", StringComparison.OrdinalIgnoreCase))
            return new WorkflowCallerCredential("Bearer");
        if (!auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var bearerToken = auth[BearerPrefix.Length..].Trim();
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(bearerToken);
        if (parsed.IsValid)
            return new WorkflowCallerCredential(parsed.NormalizedBearerToken);
        if (parsed.IsInvalid)
            return new WorkflowCallerCredential(bearerToken);

        return new WorkflowCallerCredential("Bearer");
    }
}
