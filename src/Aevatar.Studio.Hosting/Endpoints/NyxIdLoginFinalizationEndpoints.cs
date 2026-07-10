using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgentService.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.Endpoints;

public static class NyxIdLoginFinalizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/nyxid/config", HandleConfigAsync)
            .WithTags("Auth")
            .AllowAnonymous()
            .Produces<NyxIdLoginConfigurationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/auth/nyxid/finalize", HandleFinalizeAsync)
            .WithTags("Auth")
            .WithEndpointAudit(
                "identity.login.finalize",
                AuditSensitivityLevel.Confidential,
                "external_identity_binding",
                EndpointAuditTargetResolvers.Static("external_identity_binding", "login-finalize"),
                captureUnauthenticated: true)
            .AllowAnonymous()
            .Produces<NyxIdLoginFinalizationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> HandleConfigAsync(
        [FromServices] IAevatarOAuthClientProvider oauthClientProvider,
        CancellationToken ct = default)
    {
        try
        {
            var snapshot = await oauthClientProvider.GetAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NyxIdLoginConfigurationResponse(
                BaseUrl: snapshot.NyxIdAuthority.TrimEnd('/'),
                ClientId: snapshot.ClientId,
                Scope: string.IsNullOrWhiteSpace(snapshot.OauthScope)
                    ? AevatarOAuthClientScopes.AuthorizationScope
                    : snapshot.OauthScope.Trim()));
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return Results.Json(new
            {
                error = "oauth_client_not_provisioned",
                detail = "Aevatar OAuth client has not been provisioned at NyxID yet.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static async Task<IResult> HandleFinalizeAsync(
        NyxIdLoginFinalizationRequest request,
        [FromServices] INyxIdBrokerCallbackClient brokerCallback,
        [FromServices] INyxIdCapabilityBroker capabilityBroker,
        [FromServices] IExternalIdentityBindingQueryPort bindingQueryPort,
        [FromServices] ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        [FromServices] ICommandDispatchService<RefreshBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingRefreshDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Studio.NyxIdLoginFinalization");

        if (string.IsNullOrWhiteSpace(request.Code))
            return Results.BadRequest(new { error = "code_missing" });
        if (string.IsNullOrWhiteSpace(request.CodeVerifier))
            return Results.BadRequest(new { error = "code_verifier_missing" });
        if (string.IsNullOrWhiteSpace(request.RedirectUri))
            return Results.BadRequest(new { error = "redirect_uri_missing" });

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await brokerCallback
                .ExchangeAuthorizationCodeAsync(request.Code.Trim(), request.CodeVerifier.Trim(), request.RedirectUri.Trim(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization authorization-code exchange failed.");
            return Results.Json(new
            {
                error = "token_exchange_failed",
                detail = "NyxID login finalization failed during authorization-code exchange.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        if (string.IsNullOrWhiteSpace(exchange.BindingId))
        {
            return Results.Json(new
            {
                error = "broker_capability_disabled",
                detail = "NyxID did not return a broker binding id for this login.",
            }, statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(exchange.AccessToken))
        {
            return Results.Json(new
            {
                error = "access_token_missing",
                detail = "NyxID did not return an access token for this login.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        var user = ResolveUserInfo(exchange.IdToken);
        if (string.IsNullOrWhiteSpace(user.Sub))
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "subject_missing",
                detail = "NyxID login finalization could not resolve a stable user subject.",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        var subject = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = user.Sub.Trim(),
        };

        var existingBinding = await bindingQueryPort.ResolveAsync(subject, ct).ConfigureAwait(false);
        if (existingBinding != null)
        {
            if (string.Equals(existingBinding.Value, exchange.BindingId, StringComparison.Ordinal))
                return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: false));

            var probeResult = await ProbeExistingBindingAsync(capabilityBroker, subject, logger, ct).ConfigureAwait(false);
            if (probeResult == ExistingBindingProbeResult.Usable)
            {
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: false));
            }

            if (probeResult == ExistingBindingProbeResult.Unavailable)
            {
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Json(new
                {
                    error = "binding_probe_failed",
                    detail = "NyxID owner binding could not be verified; retry login finalization later.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var refreshResult = await DispatchRefreshBindingAsync(bindingRefreshDispatch, subject, exchange.BindingId, logger, ct).ConfigureAwait(false);
            if (refreshResult != BindingDispatchOutcome.Accepted)
            {
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Json(new
                {
                    error = refreshResult == BindingDispatchOutcome.Rejected ? "actor_dispatch_rejected" : "actor_dispatch_failed",
                    detail = "Stale NyxID owner binding could not be queued for local refresh.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: true));
        }

        var commitResult = await DispatchCommitBindingAsync(bindingDispatch, subject, exchange.BindingId, logger, ct).ConfigureAwait(false);
        if (commitResult != BindingDispatchOutcome.Accepted)
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = commitResult == BindingDispatchOutcome.Rejected ? "actor_dispatch_rejected" : "actor_dispatch_failed",
                detail = "NyxID owner binding could not be queued for local persistence.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(BuildResponse(exchange, user, bindingDispatchAccepted: true));
    }

    private enum ExistingBindingProbeResult
    {
        Usable,
        Stale,
        Unavailable,
    }

    private enum BindingDispatchOutcome
    {
        Accepted,
        Rejected,
        Failed,
    }

    private static async Task<ExistingBindingProbeResult> ProbeExistingBindingAsync(
        INyxIdCapabilityBroker capabilityBroker,
        ExternalSubjectRef subject,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await capabilityBroker
                .IssueShortLivedAsync(subject, new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy }, ct)
                .ConfigureAwait(false);
            return ExistingBindingProbeResult.Usable;
        }
        catch (BindingRevokedException ex)
        {
            logger.LogInformation(ex, "NyxID owner binding is stale for {Platform}:{Tenant}:{User}; refreshing local binding.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return ExistingBindingProbeResult.Stale;
        }
        catch (BindingNotFoundException ex)
        {
            logger.LogInformation(ex, "NyxID owner binding disappeared for {Platform}:{Tenant}:{User}; refreshing local binding.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return ExistingBindingProbeResult.Stale;
        }
        catch (BindingScopeMismatchException ex)
        {
            logger.LogInformation(ex, "NyxID owner binding lacks required scope for {Platform}:{Tenant}:{User}; refreshing local binding.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return ExistingBindingProbeResult.Stale;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NyxID owner binding probe failed for {Platform}:{Tenant}:{User}.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return ExistingBindingProbeResult.Unavailable;
        }
    }

    private static async Task<BindingDispatchOutcome> DispatchCommitBindingAsync(
        ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        ExternalSubjectRef subject,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var accepted = await bindingDispatch.DispatchAsync(new CommitBindingCommand
            {
                ExternalSubject = subject,
                BindingId = bindingId.Trim(),
            }, ct).ConfigureAwait(false);

            if (accepted.Succeeded && accepted.Receipt != null)
                return BindingDispatchOutcome.Accepted;

            logger.LogError("NyxID login finalization binding dispatch rejected: error={Error}.", accepted.Error);
            return BindingDispatchOutcome.Rejected;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization failed to dispatch CommitBindingCommand for {Platform}:{Tenant}:{User}.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return BindingDispatchOutcome.Failed;
        }
    }

    private static async Task<BindingDispatchOutcome> DispatchRefreshBindingAsync(
        ICommandDispatchService<RefreshBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingRefreshDispatch,
        ExternalSubjectRef subject,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var accepted = await bindingRefreshDispatch.DispatchAsync(new RefreshBindingCommand
            {
                ExternalSubject = subject,
                BindingId = bindingId.Trim(),
                Reason = "nyxid_login_refresh",
            }, ct).ConfigureAwait(false);

            if (accepted.Succeeded && accepted.Receipt != null)
                return BindingDispatchOutcome.Accepted;

            logger.LogError("NyxID login finalization stale binding refresh dispatch rejected: error={Error}.", accepted.Error);
            return BindingDispatchOutcome.Rejected;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization failed to dispatch RefreshBindingCommand for {Platform}:{Tenant}:{User}.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return BindingDispatchOutcome.Failed;
        }
    }

    private static NyxIdLoginFinalizationResponse BuildResponse(
        BrokerAuthorizationCodeResult exchange,
        NyxIdFinalizedUserInfo user,
        bool bindingDispatchAccepted) =>
        new(
            Tokens: new NyxIdFinalizedTokenSet(
                AccessToken: exchange.AccessToken ?? string.Empty,
                RefreshToken: exchange.RefreshToken,
                TokenType: string.IsNullOrWhiteSpace(exchange.TokenType) ? "Bearer" : exchange.TokenType,
                ExpiresIn: exchange.ExpiresIn ?? 3600,
                IdToken: exchange.IdToken,
                Scope: exchange.Scope),
            User: user,
            BindingDispatchAccepted: bindingDispatchAccepted);

    private static NyxIdFinalizedUserInfo ResolveUserInfo(string? idToken)
    {
        var payload = TryReadJwtPayload(idToken);
        return new NyxIdFinalizedUserInfo(
            Sub: ReadString(payload, "uid") ?? ReadString(payload, "sub") ?? string.Empty,
            Email: ReadString(payload, "email"),
            EmailVerified: ReadBool(payload, "email_verified"),
            Name: ReadString(payload, "name"),
            Picture: ReadString(payload, "picture"),
            Roles: ReadStringArray(payload, "roles"),
            Groups: ReadStringArray(payload, "groups"),
            Permissions: ReadStringArray(payload, "permissions"));
    }

    private static JsonElement? TryReadJwtPayload(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return null;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement? payload, string propertyName)
    {
        if (payload is not { } element)
            return null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return null;

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static async Task TryRevokeOrphanBindingAsync(
        INyxIdBrokerCallbackClient brokerCallback,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await brokerCallback.RevokeBindingByIdAsync(bindingId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to revoke orphan NyxID login binding {BindingId}.", bindingId);
        }
    }
}

public sealed record NyxIdLoginConfigurationResponse(
    string BaseUrl,
    string ClientId,
    string Scope);

public sealed record NyxIdLoginFinalizationRequest
{
    public string? Code { get; init; }
    public string? CodeVerifier { get; init; }
    public string? RedirectUri { get; init; }
}

public sealed record NyxIdLoginFinalizationResponse(
    NyxIdFinalizedTokenSet Tokens,
    NyxIdFinalizedUserInfo User,
    bool BindingDispatchAccepted);

public sealed record NyxIdFinalizedTokenSet(
    string AccessToken,
    string? RefreshToken,
    string TokenType,
    int ExpiresIn,
    string? IdToken,
    string? Scope);

public sealed record NyxIdFinalizedUserInfo(
    string Sub,
    string? Email = null,
    bool? EmailVerified = null,
    string? Name = null,
    string? Picture = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<string>? Permissions = null);
