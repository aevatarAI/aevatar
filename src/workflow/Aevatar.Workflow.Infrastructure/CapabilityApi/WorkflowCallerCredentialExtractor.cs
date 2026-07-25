using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var token = ExtractCredentialToken(http);
        if (!token.Succeeded)
            return ValueTask.FromResult(Invalid());
        return token.RawToken == null
            ? ValueTask.FromResult(WorkflowCallerCredentialExtractionResult.Success(null))
            : ParseCredentialAsync(token.RawToken, http, bindingQueryPort, logger, ct);
    }

    private static CallerCredentialTokenExtractionResult ExtractCredentialToken(HttpContext? http)
    {
        if (http?.Request.Headers.TryGetValue("Authorization", out var authorizationValues) == true)
        {
            if (authorizationValues.Count != 1)
                return CallerCredentialTokenExtractionResult.Invalid;

            var authorization = authorizationValues[0];
            if (string.Equals(
                    authorization?.Trim(),
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CallerCredentialTokenExtractionResult.Invalid;
            }

            return authorization?.StartsWith(
                       BearerPrefix,
                       StringComparison.OrdinalIgnoreCase) == true
                ? CallerCredentialTokenExtractionResult.Success(
                    authorization[BearerPrefix.Length..])
                : CallerCredentialTokenExtractionResult.Invalid;
        }

        if (http?.Request.Headers.TryGetValue(
                NyxIdDelegationTokenHeader,
                out var delegationValues) == true)
        {
            return delegationValues.Count != 1
                ? CallerCredentialTokenExtractionResult.Invalid
                : CallerCredentialTokenExtractionResult.Success(delegationValues[0]);
        }

        return CallerCredentialTokenExtractionResult.Missing;
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
        ILogger? logger,
        CancellationToken ct)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(rawToken);
        if (parsed.IsValid)
        {
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    await ResolveAuthenticatedNyxIdAuthorityAsync(http, bindingQueryPort, logger, ct)));
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
        ILogger? logger,
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
        var bindingId = await ResolveBindingIdAsync(bindingQueryPort, externalUserId, tenant, logger, ct);
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
        ILogger? logger,
        CancellationToken ct)
    {
        if (bindingQueryPort == null)
            return null;

        var subject = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = tenant,
            ExternalUserId = externalUserId,
        };

        try
        {
            var bindingId = await bindingQueryPort.ResolveAsync(subject, ct);
            return string.IsNullOrWhiteSpace(bindingId?.Value)
                ? null
                : bindingId.Value.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Caller NyxID binding lookup failed; continuing workflow chat without verified sender binding. subject={Platform}:{Tenant}:{User}",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return null;
        }
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
