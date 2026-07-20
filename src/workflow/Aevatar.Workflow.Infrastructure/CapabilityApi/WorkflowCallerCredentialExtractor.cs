using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Microsoft.AspNetCore.Http;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";
    private const string NyxIdDelegationTokenHeader = "X-NyxID-Delegation-Token";
    private const string DefaultNyxIdCapabilityScope = "proxy";

    public static WorkflowCallerCredentialExtractionResult Extract(HttpContext? http)
    {
        if (http?.Request.Headers.TryGetValue(NyxIdDelegationTokenHeader, out var delegationValues) == true)
        {
            if (delegationValues.Count != 1)
                return Invalid();

            return ParseCredential(delegationValues[0], http);
        }

        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null)
            return WorkflowCallerCredentialExtractionResult.Success(null);
        if (string.Equals(auth.Trim(), "Bearer", StringComparison.OrdinalIgnoreCase))
            return Invalid();
        if (!auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return WorkflowCallerCredentialExtractionResult.Success(null);

        return ParseCredential(auth[BearerPrefix.Length..], http);
    }

    private static WorkflowCallerCredentialExtractionResult ParseCredential(
        string? rawToken,
        HttpContext? http)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(rawToken);
        if (parsed.IsValid)
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    ResolveAuthenticatedNyxIdAuthority(http)));

        return Invalid();
    }

    private static WorkflowCallerNyxIdAuthority? ResolveAuthenticatedNyxIdAuthority(HttpContext? http)
    {
        var principal = http?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var externalUserId = ReadFirstClaim(
            principal,
            "scope_id",
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

    private static WorkflowCallerCredentialExtractionResult Invalid() =>
        WorkflowCallerCredentialExtractionResult.Failure(WorkflowChatRunStartError.InvalidCallerCredential);
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
