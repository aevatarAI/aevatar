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
            : ParseCredential(token, http);
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
            : ParseCredentialAsync(token, http, bindingQueryPort, logger, ct);
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
                    authorization[BearerPrefix.Length..],
                    WorkflowProtocol.NyxIdCallerCredentialKind.SourceReadableUserBearer)
                : CallerCredentialTokenExtractionResult.Invalid;
        }

        if (http?.Request.Headers.TryGetValue(
                NyxIdDelegationTokenHeader,
                out var delegationValues) == true)
        {
            return delegationValues.Count != 1
                ? CallerCredentialTokenExtractionResult.Invalid
                : CallerCredentialTokenExtractionResult.Success(
                    delegationValues[0],
                    WorkflowProtocol.NyxIdCallerCredentialKind.ProxyDelegation);
        }

        return CallerCredentialTokenExtractionResult.Missing;
    }

    private static WorkflowCallerCredentialExtractionResult ParseCredential(
        CallerCredentialTokenExtractionResult token,
        HttpContext? http)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(token.RawToken);
        if (parsed.IsValid)
        {
            var selection = CreateSelection(token.Kind, parsed.NormalizedBearerToken!);
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    ResolveAuthenticatedNyxIdAuthority(http)),
                selection);
        }

        return Invalid();
    }

    private static async ValueTask<WorkflowCallerCredentialExtractionResult> ParseCredentialAsync(
        CallerCredentialTokenExtractionResult token,
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        ILogger? logger,
        CancellationToken ct)
    {
        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(token.RawToken);
        if (parsed.IsValid)
        {
            var selection = CreateSelection(token.Kind, parsed.NormalizedBearerToken!);
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    parsed.NormalizedBearerToken,
                    await ResolveAuthenticatedNyxIdAuthorityAsync(http, bindingQueryPort, logger, ct)),
                selection);
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

    private static WorkflowProtocol.NyxIdCallerCredentialSelection CreateSelection(
        WorkflowProtocol.NyxIdCallerCredentialKind kind,
        string bearerToken) => kind switch
        {
            WorkflowProtocol.NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                WorkflowProtocol.NyxIdCallerCredentialSelection.SourceReadableUserBearer(bearerToken),
            WorkflowProtocol.NyxIdCallerCredentialKind.ProxyDelegation =>
                WorkflowProtocol.NyxIdCallerCredentialSelection.ProxyDelegation(bearerToken),
            _ => throw new InvalidOperationException("Caller credential kind is invalid."),
        };
}

readonly record struct CallerCredentialTokenExtractionResult(
    string? RawToken,
    WorkflowProtocol.NyxIdCallerCredentialKind Kind,
    bool Succeeded)
{
    public static CallerCredentialTokenExtractionResult Missing =>
        new(null, WorkflowProtocol.NyxIdCallerCredentialKind.Unspecified, true);

    public static CallerCredentialTokenExtractionResult Invalid =>
        new(null, WorkflowProtocol.NyxIdCallerCredentialKind.Unspecified, false);

    public static CallerCredentialTokenExtractionResult Success(
        string? rawToken,
        WorkflowProtocol.NyxIdCallerCredentialKind kind) => new(rawToken, kind, true);
}

public readonly record struct WorkflowCallerCredentialExtractionResult(
    WorkflowCallerCredential? Credential,
    WorkflowProtocol.NyxIdCallerCredentialSelection? NyxIdCredentialSelection,
    WorkflowChatRunStartError Error)
{
    public bool Succeeded => Error == WorkflowChatRunStartError.None;

    public static WorkflowCallerCredentialExtractionResult Success(WorkflowCallerCredential? credential) =>
        new(credential, null, WorkflowChatRunStartError.None);

    public static WorkflowCallerCredentialExtractionResult Success(
        WorkflowCallerCredential credential,
        WorkflowProtocol.NyxIdCallerCredentialSelection nyxIdCredentialSelection) =>
        new(credential, nyxIdCredentialSelection, WorkflowChatRunStartError.None);

    public static WorkflowCallerCredentialExtractionResult Failure(WorkflowChatRunStartError error) =>
        new(null, null, error);
}

public sealed class WorkflowCallerCredentialSelectionException : InvalidOperationException
{
    public const string ErrorCode = "INVALID_WORKFLOW_CALLER_CREDENTIAL";
    public const string SafeMessage = "Caller credential selection is invalid.";

    public WorkflowCallerCredentialSelectionException()
        : base(SafeMessage)
    {
    }
}
