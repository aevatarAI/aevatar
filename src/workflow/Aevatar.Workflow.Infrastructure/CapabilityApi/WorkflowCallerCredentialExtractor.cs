using System.Security.Claims;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aevatar.Workflow.Application.Abstractions.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class WorkflowCallerCredentialExtractor
{
    private const string BearerPrefix = "Bearer ";
    private const string JwtBearerAuthenticationScheme = "Bearer";
    private const string NyxIdIdentityAssertionAuthenticationScheme = "NyxIdIdentityAssertion";
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
        return ExtractAsync(http, bindingQueryPort, null, logger, ct);
    }

    public static ValueTask<WorkflowCallerCredentialExtractionResult> ExtractAsync(
        HttpContext? http,
        IExternalIdentityBindingQueryPort? bindingQueryPort,
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var tokens = ExtractCredentialTokens(http);
        if (!tokens.Succeeded)
            return ValueTask.FromResult(Invalid());
        return tokens.ExecutionRawToken == null
            ? ValueTask.FromResult(WorkflowCallerCredentialExtractionResult.Success(null))
            : ParseCredentialAsync(
                tokens,
                http,
                bindingQueryPort,
                callerAccessTokenProvider,
                logger,
                ct);
    }

    public static ValueTask<WorkflowCallerCredentialExtractionResult> ExtractAsync(
        HttpContext? http,
        CancellationToken ct = default)
    {
        var services = http?.Features.Get<IServiceProvidersFeature>()?.RequestServices;
        var loggerFactory = services?.GetService<ILoggerFactory>();
        return ExtractAsync(
            http,
            services?.GetService<IExternalIdentityBindingQueryPort>(),
            services?.GetService<IWorkflowCallerAccessTokenProvider>(),
            loggerFactory?.CreateLogger("Aevatar.Workflow.CallerCredential"),
            ct);
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
            var selection = CreateAdmissionSelection(tokens, execution, sourceReadable, http);
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
        IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider,
        ILogger? logger,
        CancellationToken ct)
    {
        var execution = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(tokens.ExecutionRawToken);
        var sourceReadable = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(
            tokens.SourceReadableRawToken);
        if (execution.IsValid && !sourceReadable.IsInvalid)
        {
            var authority = await ResolveAuthenticatedNyxIdAuthorityAsync(
                http,
                bindingQueryPort,
                logger,
                ct);
            if (!sourceReadable.IsValid &&
                tokens.ExecutionKind == WorkflowProtocol.NyxIdCallerCredentialKind.ProxyDelegation &&
                !string.IsNullOrWhiteSpace(authority?.BindingId) &&
                callerAccessTokenProvider != null)
            {
                var issuedToken = await TryIssueSourceReadableTokenAsync(
                    callerAccessTokenProvider,
                    authority,
                    logger,
                    ct);
                sourceReadable = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(issuedToken);
            }

            var selection = CreateAdmissionSelection(tokens, execution, sourceReadable, http);
            return WorkflowCallerCredentialExtractionResult.Success(
                new WorkflowCallerCredential(
                    execution.NormalizedBearerToken,
                    authority,
                    tokens.ExecutionKind,
                    sourceReadable.NormalizedBearerToken),
                selection);
        }

        return Invalid();
    }

    private static async ValueTask<string?> TryIssueSourceReadableTokenAsync(
        IWorkflowCallerAccessTokenProvider callerAccessTokenProvider,
        WorkflowCallerNyxIdAuthority authority,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            return await callerAccessTokenProvider.IssueAsync(
                new WorkflowProtocol.WorkflowCallerNyxIdAuthority
                {
                    Platform = authority.Platform,
                    Tenant = authority.Tenant,
                    ExternalUserId = authority.ExternalUserId,
                    Scope = authority.Scope,
                    BindingId = authority.BindingId ?? string.Empty,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Caller NyxID source-readable token exchange failed; continuing with the verified proxy delegation.");
            return null;
        }
    }

    private static WorkflowCallerNyxIdAuthority? ResolveAuthenticatedNyxIdAuthority(HttpContext? http)
    {
        var principal = http?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var externalUserId = ReadNyxIdExternalUserId(principal);
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

        var externalUserId = ReadNyxIdExternalUserId(principal);
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

    private static string? ReadNyxIdExternalUserId(ClaimsPrincipal principal)
    {
        var userId = ReadFirstClaim(principal, "uid", "user_id");
        if (!string.IsNullOrWhiteSpace(userId))
            return userId;

        if (string.Equals(
                principal.Identity?.AuthenticationType,
                NyxIdIdentityAssertionAuthenticationScheme,
                StringComparison.Ordinal))
        {
            return null;
        }

        return ReadFirstClaim(principal, "sub", ClaimTypes.NameIdentifier);
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
        WorkflowProtocol.WorkflowCallerCredentialTokenParseResult sourceReadable,
        HttpContext? http)
    {
        if (sourceReadable.IsValid)
        {
            return !string.IsNullOrWhiteSpace(tokens.SourceReadableRawToken) &&
                   IsAuthenticatedHumanAccessToken(http)
                ? WorkflowProtocol.NyxIdCallerCredentialSelection.DirectUserBearer(
                    sourceReadable.NormalizedBearerToken!)
                : WorkflowProtocol.NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    sourceReadable.NormalizedBearerToken!);
        }

        if (tokens.ExecutionKind == WorkflowProtocol.NyxIdCallerCredentialKind.SourceReadableUserBearer)
        {
            return IsAuthenticatedHumanAccessToken(http)
                ? WorkflowProtocol.NyxIdCallerCredentialSelection.DirectUserBearer(
                    execution.NormalizedBearerToken!)
                : WorkflowProtocol.NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    execution.NormalizedBearerToken!);
        }

        return CreateSelection(tokens.ExecutionKind, execution.NormalizedBearerToken!);
    }

    private static bool IsAuthenticatedHumanAccessToken(HttpContext? http)
    {
        var authentication = http?.Features
            .Get<IAuthenticateResultFeature>()?
            .AuthenticateResult;
        var ticket = authentication?.Ticket;
        var principal = ticket?.Principal;
        if (authentication?.Succeeded != true ||
            ticket == null ||
            !string.Equals(
                ticket.AuthenticationScheme,
                JwtBearerAuthenticationScheme,
                StringComparison.Ordinal) ||
            principal?.Identity?.IsAuthenticated != true ||
            !HasExactClaim(principal, "token_type", "access") ||
            HasTrueClaim(principal, "delegated") ||
            HasTrueClaim(principal, "sa") ||
            HasTrueClaim(principal, "relay") ||
            HasTrueClaim(principal, "assistant_forward") ||
            HasTrueClaim(principal, "aevatar.scope_service") ||
            HasNonEmptyClaim(principal, "act"))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ReadFirstClaim(
            principal,
            "uid",
            "sub",
            ClaimTypes.NameIdentifier,
            "user_id"));
    }

    private static bool HasExactClaim(
        ClaimsPrincipal principal,
        string claimType,
        string expectedValue) =>
        principal.Claims.Any(claim =>
            string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
            string.Equals(claim.Value?.Trim(), expectedValue, StringComparison.Ordinal));

    private static bool HasTrueClaim(ClaimsPrincipal principal, string claimType) =>
        HasExactClaim(principal, claimType, bool.TrueString.ToLowerInvariant()) ||
        HasExactClaim(principal, claimType, bool.TrueString);

    private static bool HasNonEmptyClaim(ClaimsPrincipal principal, string claimType) =>
        principal.Claims.Any(claim =>
            string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(claim.Value));
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
