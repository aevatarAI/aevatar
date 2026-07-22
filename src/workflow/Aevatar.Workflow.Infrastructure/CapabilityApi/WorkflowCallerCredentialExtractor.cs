using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
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
        var token = ExtractCredentialToken(http);
        if (!token.Succeeded)
            return Invalid();
        return token.RawToken == null
            ? WorkflowCallerCredentialExtractionResult.Success(null)
            : ParseCredential(token.RawToken, http);
    }

    public static ValueTask<WorkflowCallerCredentialExtractionResult> ExtractAsync(
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        CancellationToken ct = default)
    {
        var token = ExtractCredentialToken(http);
        if (!token.Succeeded)
            return ValueTask.FromResult(Invalid());
        return token.RawToken == null
            ? ValueTask.FromResult(WorkflowCallerCredentialExtractionResult.Success(null))
            : ParseCredentialAsync(token.RawToken, http, bindingQueryPort, ct);
    }

    private static CallerCredentialTokenExtractionResult ExtractCredentialToken(HttpContext? http)
    {
        if (http?.Request.Headers.TryGetValue(NyxIdDelegationTokenHeader, out var delegationValues) == true)
        {
            return delegationValues.Count != 1
                ? CallerCredentialTokenExtractionResult.Invalid
                : CallerCredentialTokenExtractionResult.Success(delegationValues[0]);
        }

        var auth = http?.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null)
            return CallerCredentialTokenExtractionResult.Missing;
        if (string.Equals(auth.Trim(), "Bearer", StringComparison.OrdinalIgnoreCase))
            return CallerCredentialTokenExtractionResult.Invalid;
        return auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? CallerCredentialTokenExtractionResult.Success(auth[BearerPrefix.Length..])
            : CallerCredentialTokenExtractionResult.Missing;
    }

    private static WorkflowCallerCredentialExtractionResult ParseCredential(
        string? rawToken,
        HttpContext? http)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(rawToken);
        if (parsed.IsValid)
        {
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    ResolveAuthenticatedNyxIdAuthority(http)));
        }

        return Invalid();
    }

    private static async ValueTask<WorkflowCallerCredentialExtractionResult> ParseCredentialAsync(
        string? rawToken,
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        CancellationToken ct)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(rawToken);
        if (parsed.IsValid)
        {
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    await ResolveAuthenticatedNyxIdAuthorityAsync(http, bindingQueryPort, ct)));
        }

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

    private static async ValueTask<WorkflowCallerNyxIdAuthority?> ResolveAuthenticatedNyxIdAuthorityAsync(
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        CancellationToken ct)
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
        if (string.IsNullOrWhiteSpace(externalUserId))
            return null;

        const string tenant = "";
        var bindingId = await ResolveBindingIdAsync(bindingQueryPort, externalUserId, tenant, ct);
        return new WorkflowCallerNyxIdAuthority(
            OwnerScope.NyxIdPlatform,
            tenant,
            externalUserId,
            DefaultNyxIdCapabilityScope,
            bindingId);
    }

    private static async ValueTask<string?> ResolveBindingIdAsync(
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        string externalUserId,
        string tenant,
        CancellationToken ct)
    {
        if (bindingQueryPort == null)
            return null;

        var bindingId = await bindingQueryPort.ResolveAsync(
            new ExternalSubjectRef
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = tenant,
                ExternalUserId = externalUserId,
            },
            ct);
        return string.IsNullOrWhiteSpace(bindingId?.Value)
            ? null
            : bindingId.Value.Trim();
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

readonly record struct CallerCredentialTokenExtractionResult(string? RawToken, bool Succeeded)
{
    public static CallerCredentialTokenExtractionResult Missing => new(null, true);
    public static CallerCredentialTokenExtractionResult Invalid => new(null, false);
    public static CallerCredentialTokenExtractionResult Success(string? rawToken) => new(rawToken, true);
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
