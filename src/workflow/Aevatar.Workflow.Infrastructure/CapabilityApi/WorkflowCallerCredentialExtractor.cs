using Microsoft.AspNetCore.Http;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";

    public static WorkflowCallerCredentialExtractionResult Extract(HttpContext? http)
    {
        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null)
            return WorkflowCallerCredentialExtractionResult.Success(null);
        if (string.Equals(auth.Trim(), "Bearer", StringComparison.OrdinalIgnoreCase))
            return WorkflowCallerCredentialExtractionResult.Failure(WorkflowChatRunStartError.InvalidCallerCredential);
        if (!auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return WorkflowCallerCredentialExtractionResult.Success(null);

        var bearerToken = auth[BearerPrefix.Length..].Trim();
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(bearerToken);
        if (parsed.IsValid)
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(parsed.NormalizedBearerToken));

        return WorkflowCallerCredentialExtractionResult.Failure(WorkflowChatRunStartError.InvalidCallerCredential);
    }
}

public readonly record struct WorkflowCallerCredentialExtractionResult(
    WorkflowCallerCredential? Credential,
    WorkflowChatRunStartError Error)
{
    public bool Succeeded => Error == WorkflowChatRunStartError.None;

    public static WorkflowCallerCredentialExtractionResult Success(WorkflowCallerCredential? credential) =>
        new(credential, WorkflowChatRunStartError.None);

    public static WorkflowCallerCredentialExtractionResult Failure(WorkflowChatRunStartError error) =>
        new(null, error);
}
