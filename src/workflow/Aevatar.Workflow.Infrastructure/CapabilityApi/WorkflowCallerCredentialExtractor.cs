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
        var tokens = ExtractCredentialTokens(http);
        if (!tokens.Succeeded)
            return Invalid();
        return tokens.ExecutionRawToken == null
            ? WorkflowCallerCredentialExtractionResult.Success(null)
            : ParseCredential(tokens, http);
    }

    public static ValueTask<WorkflowCallerCredentialExtractionResult> ExtractAsync(
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var tokens = ExtractCredentialTokens(http);
        if (!tokens.Succeeded)
            return ValueTask.FromResult(Invalid());
        return tokens.ExecutionRawToken == null
            ? ValueTask.FromResult(WorkflowCallerCredentialExtractionResult.Success(null))
            : ParseCredentialAsync(tokens, http, bindingQueryPort, logger, ct);
    }

    private static CallerCredentialTokensExtractionResult ExtractCredentialTokens(HttpContext? http)
    {
        string? sourceReadableRawToken = null;
        if (http?.Request.Headers.TryGetValue("Authorization", out var authorizationValues) == true)
        {
            if (authorizationValues.Count != 1)
                return CallerCredentialTokensExtractionResult.Invalid;

            var authorization = authorizationValues[0];
            if (string.Equals(
                    authorization?.Trim(),
                    "Bearer",
                    StringComparison.OrdinalIgnoreCase))
            {
                return CallerCredentialTokensExtractionResult.Invalid;
            }

            if (authorization?.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) != true)
                return CallerCredentialTokensExtractionResult.Invalid;

            sourceReadableRawToken = authorization[BearerPrefix.Length..];
        }

        string? delegationRawToken = null;
        if (http?.Request.Headers.TryGetValue(
                NyxIdDelegationTokenHeader,
                out var delegationValues) == true)
        {
            if (delegationValues.Count != 1)
                return CallerCredentialTokensExtractionResult.Invalid;
            delegationRawToken = delegationValues[0];
        }

        if (delegationRawToken != null)
        {
            return CallerCredentialTokensExtractionResult.Success(
                delegationRawToken,
                WorkflowProtocol.NyxIdCallerCredentialKind.ProxyDelegation,
                sourceReadableRawToken);
        }

        return sourceReadableRawToken == null
            ? CallerCredentialTokensExtractionResult.Missing
            : CallerCredentialTokensExtractionResult.Success(
                sourceReadableRawToken,
                WorkflowProtocol.NyxIdCallerCredentialKind.SourceReadableUserBearer,
                null);
    }

    private static WorkflowCallerCredentialExtractionResult ParseCredential(
        CallerCredentialTokensExtractionResult tokens,
        HttpContext? http)
    {
        var execution = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(tokens.ExecutionRawToken);
        var sourceReadable = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(
            tokens.SourceReadableRawToken);
        if (execution.IsValid && !sourceReadable.IsInvalid)
        {
            var selection = CreateAdmissionSelection(tokens, execution, sourceReadable);
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    execution.NormalizedBearerToken,
                    ResolveAuthenticatedNyxIdAuthority(http),
                    tokens.ExecutionKind,
                    sourceReadable.NormalizedBearerToken),
                selection);
        }

        return Invalid();
    }

    private static async ValueTask<WorkflowCallerCredentialExtractionResult> ParseCredentialAsync(
        CallerCredentialTokensExtractionResult tokens,
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        ILogger? logger,
        CancellationToken ct)
    {
        var execution = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(tokens.ExecutionRawToken);
        var sourceReadable = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(
            tokens.SourceReadableRawToken);
        if (execution.IsValid && !sourceReadable.IsInvalid)
        {
            var selection = CreateAdmissionSelection(tokens, execution, sourceReadable);
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    execution.NormalizedBearerToken,
                    await ResolveAuthenticatedNyxIdAuthorityAsync(http, bindingQueryPort, logger, ct),
                    tokens.ExecutionKind,
                    sourceReadable.NormalizedBearerToken),
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

    private static WorkflowProtocol.NyxIdCallerCredentialSelection CreateAdmissionSelection(
        CallerCredentialTokensExtractionResult tokens,
        WorkflowProtocol.WorkflowCallerCredentialTokenParseResult execution,
        WorkflowProtocol.WorkflowCallerCredentialTokenParseResult sourceReadable) =>
        sourceReadable.IsValid
            ? WorkflowProtocol.NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                sourceReadable.NormalizedBearerToken!)
            : CreateSelection(tokens.ExecutionKind, execution.NormalizedBearerToken!);
}

readonly record struct CallerCredentialTokensExtractionResult(
    string? ExecutionRawToken,
    WorkflowProtocol.NyxIdCallerCredentialKind ExecutionKind,
    string? SourceReadableRawToken,
    bool Succeeded)
{
    public static CallerCredentialTokensExtractionResult Missing =>
        new(null, WorkflowProtocol.NyxIdCallerCredentialKind.Unspecified, null, true);

    public static CallerCredentialTokensExtractionResult Invalid =>
        new(null, WorkflowProtocol.NyxIdCallerCredentialKind.Unspecified, null, false);

    public static CallerCredentialTokensExtractionResult Success(
        string executionRawToken,
        WorkflowProtocol.NyxIdCallerCredentialKind executionKind,
        string? sourceReadableRawToken) =>
        new(executionRawToken, executionKind, sourceReadableRawToken, true);
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
