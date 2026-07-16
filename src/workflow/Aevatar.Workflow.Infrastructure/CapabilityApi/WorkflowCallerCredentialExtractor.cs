using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Microsoft.AspNetCore.Http;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";
    private const string DefaultNyxIdCapabilityScope = "proxy";

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
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    ResolveAuthenticatedNyxIdAuthority(http)));

        return WorkflowCallerCredentialExtractionResult.Failure(WorkflowChatRunStartError.InvalidCallerCredential);
    }

    private static WorkflowCallerNyxIdAuthority? ResolveAuthenticatedNyxIdAuthority(HttpContext? http)
    {
        var principal = http?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var externalUserId = ReadFirstClaim(
            principal,
            "uid",
            "sub",
            ClaimTypes.NameIdentifier,
            "user_id");
        return string.IsNullOrWhiteSpace(externalUserId)
            ? null
            : new WorkflowCallerNyxIdAuthority(
                OwnerScope.NyxIdPlatform,
                string.Empty,
                externalUserId,
                DefaultNyxIdCapabilityScope);
    }

    private static string? ReadFirstClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
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
