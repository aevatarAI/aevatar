using System.Text.Json;
using System.Security.Claims;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/auth/nyxid/authorization-catalog:refresh", HandleAuthorizationCatalogRefreshAsync)
            .WithTags("Auth")
            .WithEndpointAudit(
                "identity.authorization-catalog.refresh",
                AuditSensitivityLevel.Confidential,
                "nyxid_authorization_catalog",
                EndpointAuditTargetResolvers.Static("nyxid_authorization_catalog", "current-owner"))
            .Produces<NyxIdAuthorizationCatalogRefreshResponse>(StatusCodes.Status200OK)
            .Produces<NyxIdAuthorizationCatalogRefreshResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<IResult> HandleAuthorizationCatalogRefreshAsync(
        HttpContext http,
        [FromServices] INyxIdAuthorizationCatalogRefreshPort catalogRefresh,
        [FromServices] INyxIdAuthorizationCatalogVisibilityPort catalogVisibilityPort,
        CancellationToken ct = default)
    {
        var subject = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? http.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Json(new
            {
                code = "NYXID_AUTHORIZATION_CATALOG_UNAUTHORIZED",
                message = "A verified NyxID subject is required.",
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var bearerToken = ResolveRequestBearerToken(http);
        if (bearerToken == null)
        {
            return Results.Json(new
            {
                code = "NYXID_AUTHORIZATION_CATALOG_UNAUTHORIZED",
                message = "A current NyxID bearer is required.",
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        try
        {
            var result = await catalogRefresh
                .RefreshPersonalAsync(subject.Trim(), bearerToken, ct)
                .ConfigureAwait(false);
            var owner = PersonalCatalogOwner(subject);
            var visibility = result.Success
                ? await catalogVisibilityPort.ResolveAsync(owner, result.StateVersion, ct).ConfigureAwait(false)
                : new NyxIdAuthorizationCatalogVisibilityResult(
                    NyxIdAuthorizationCatalogVisibilityStatus.Unspecified,
                    0,
                    0,
                    string.Empty);
            var response = new NyxIdAuthorizationCatalogRefreshResponse(
                visibility.Ready,
                ToCatalogRefreshStatus(result.Status),
                result.FailureCode,
                ToCatalogVisibilityStatus(visibility.Status),
                visibility.FailureCode,
                visibility.RequiredStateVersion,
                visibility.VisibleStateVersion);
            return result.Status switch
            {
                NyxIdAuthorizationCatalogRefreshStatus.Observed when visibility.Ready => Results.Ok(response),
                NyxIdAuthorizationCatalogRefreshStatus.Observed when visibility.ProjectionPending => Results.Json(
                    response,
                    statusCode: StatusCodes.Status202Accepted),
                NyxIdAuthorizationCatalogRefreshStatus.AccessDenied => Results.Json(
                    response,
                    statusCode: StatusCodes.Status403Forbidden),
                NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported => Results.Json(
                    response,
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var loggerFactory = http.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
            loggerFactory?.CreateLogger(typeof(NyxIdLoginFinalizationEndpoints).FullName!)
                .LogWarning(
                    "Failed to refresh the NyxID authorization catalog for owner kind {OwnerKind}.",
                    AuthorizationOwnerKind.Personal);
            return Results.Json(
                new NyxIdAuthorizationCatalogRefreshResponse(
                    Ready: false,
                    RefreshStatus: "failed",
                    RefreshFailureCode: "nyxid_catalog_refresh_failed",
                    VisibilityStatus: "not_evaluated",
                    VisibilityFailureCode: string.Empty,
                    RequiredStateVersion: 0,
                    VisibleStateVersion: 0),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
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
        [FromServices] ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingReplaceDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] INyxIdAuthorizationCatalogRefreshPort? catalogRefreshLifecycle = null,
        [FromServices] INyxIdAuthorizationCatalogVisibilityPort? catalogVisibilityPort = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var logger = loggerFactory.CreateLogger("Aevatar.Studio.NyxIdLoginFinalization");

        if (string.IsNullOrWhiteSpace(request.Code))
            return LoginFinalizationProblem(StatusCodes.Status400BadRequest, "code_missing", "The NyxID authorization code is required.");
        if (string.IsNullOrWhiteSpace(request.CodeVerifier))
            return LoginFinalizationProblem(StatusCodes.Status400BadRequest, "code_verifier_missing", "The PKCE code verifier is required.");
        if (string.IsNullOrWhiteSpace(request.RedirectUri))
            return LoginFinalizationProblem(StatusCodes.Status400BadRequest, "redirect_uri_missing", "The login redirect URI is required.");

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await brokerCallback
                .ExchangeAuthorizationCodeAsync(request.Code.Trim(), request.CodeVerifier.Trim(), request.RedirectUri.Trim(), ct)
                .ConfigureAwait(false);
        }
        catch (NyxIdRequiredServiceAccessException ex)
        {
            logger.LogInformation(ex, "NyxID login did not grant every required service resource.");
            return LoginFinalizationProblem(
                StatusCodes.Status409Conflict,
                "required_service_access_missing",
                "Return to login and allow access to the Aevatar and default LLM services in NyxID.");
        }
        catch (AevatarOAuthClientNotProvisionedException ex)
        {
            logger.LogError(ex, "NyxID login finalization failed: the Aevatar OAuth client is not provisioned.");
            return LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "oauth_client_not_provisioned",
                "Aevatar OAuth client has not been provisioned at NyxID yet.");
        }
        // RFC 6749 §5.2: the token endpoint answers 400 for a bad grant
        // (expired/replayed code), while 401 means the OAuth client itself is
        // rejected — a service-side provisioning fault, not a user-recoverable one.
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            logger.LogWarning(ex, "NyxID rejected the login authorization code.");
            return LoginFinalizationProblem(
                StatusCodes.Status400BadRequest,
                "authorization_code_rejected",
                "NyxID rejected the authorization code; restart login to obtain a fresh code.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization authorization-code exchange failed.");
            return LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "token_exchange_failed",
                "NyxID login finalization failed during authorization-code exchange.");
        }

        if (string.IsNullOrWhiteSpace(exchange.BindingId))
        {
            return LoginFinalizationProblem(
                StatusCodes.Status409Conflict,
                "broker_capability_disabled",
                "NyxID did not return a broker binding id for this login.");
        }

        if (string.IsNullOrWhiteSpace(exchange.AccessToken))
        {
            return LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "access_token_missing",
                "NyxID did not return an access token for this login.");
        }

        var user = ResolveUserInfo(exchange.IdToken);
        if (string.IsNullOrWhiteSpace(user.Sub))
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "subject_missing",
                "NyxID login finalization could not resolve a stable user subject.");
        }

        var subject = new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = user.Sub.Trim(),
        };
        var ownerScopeId = user.Sub.Trim();

        var existingBinding = await bindingQueryPort.ResolveAsync(subject, ct).ConfigureAwait(false);
        if (existingBinding != null)
        {
            if (string.Equals(existingBinding.Value, exchange.BindingId, StringComparison.Ordinal))
            {
                var sameBindingProbe = await ProbeIssuedBindingAsync(
                    capabilityBroker,
                    subject,
                    exchange.BindingId,
                    logger,
                    ct).ConfigureAwait(false);
                if (sameBindingProbe != IssuedBindingProbeResult.Usable)
                    return BuildIssuedBindingProbeError(sameBindingProbe);

                return await CompleteLoginAsync(
                    exchange,
                    user,
                    false,
                    catalogRefreshLifecycle,
                    catalogVisibilityPort,
                    logger,
                    ct);
            }

            var replacementReason = "studio_service_access_review";
            if (!request.ServiceAccessReview)
            {
                var probeResult = await ProbeExistingBindingAsync(capabilityBroker, subject, logger, ct).ConfigureAwait(false);
                if (probeResult == ExistingBindingProbeResult.Usable)
                {
                    await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                    return await CompleteLoginAsync(
                        exchange,
                        user,
                        false,
                        catalogRefreshLifecycle,
                        catalogVisibilityPort,
                        logger,
                        ct);
                }

                if (probeResult == ExistingBindingProbeResult.Unavailable)
                {
                    await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                    return LoginFinalizationProblem(
                        StatusCodes.Status503ServiceUnavailable,
                        "binding_probe_failed",
                        "NyxID owner binding could not be verified; retry login finalization later.");
                }

                replacementReason = "nyxid_login_recovery";
            }

            var issuedBindingProbe = await ProbeIssuedBindingAsync(
                capabilityBroker,
                subject,
                exchange.BindingId,
                logger,
                ct).ConfigureAwait(false);
            if (issuedBindingProbe != IssuedBindingProbeResult.Usable)
            {
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return BuildIssuedBindingProbeError(issuedBindingProbe);
            }

            var replaceResult = await DispatchReplaceBindingAsync(
                bindingReplaceDispatch,
                subject,
                existingBinding.Value,
                exchange.BindingId,
                ownerScopeId,
                replacementReason,
                logger,
                ct).ConfigureAwait(false);
            if (replaceResult != BindingDispatchOutcome.Accepted)
            {
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return LoginFinalizationProblem(
                    StatusCodes.Status503ServiceUnavailable,
                    replaceResult == BindingDispatchOutcome.Rejected ? "actor_dispatch_rejected" : "actor_dispatch_failed",
                    "NyxID owner binding replacement could not be queued.");
            }

            return await CompleteLoginAsync(
                exchange,
                user,
                true,
                catalogRefreshLifecycle,
                catalogVisibilityPort,
                logger,
                ct);
        }

        var newBindingProbe = await ProbeIssuedBindingAsync(
            capabilityBroker,
            subject,
            exchange.BindingId,
            logger,
            ct).ConfigureAwait(false);
        if (newBindingProbe != IssuedBindingProbeResult.Usable)
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return BuildIssuedBindingProbeError(newBindingProbe);
        }

        var commitResult = await DispatchCommitBindingAsync(
            bindingDispatch,
            subject,
            exchange.BindingId,
            ownerScopeId,
            logger,
            ct).ConfigureAwait(false);
        if (commitResult != BindingDispatchOutcome.Accepted)
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                commitResult == BindingDispatchOutcome.Rejected ? "actor_dispatch_rejected" : "actor_dispatch_failed",
                "NyxID owner binding could not be queued for local persistence.");
        }

        return await CompleteLoginAsync(
            exchange,
            user,
            true,
            catalogRefreshLifecycle,
            catalogVisibilityPort,
            logger,
            ct);
    }

    private static async Task<IResult> CompleteLoginAsync(
        BrokerAuthorizationCodeResult exchange,
        NyxIdFinalizedUserInfo user,
        bool bindingDispatchAccepted,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshLifecycle,
        INyxIdAuthorizationCatalogVisibilityPort? catalogVisibilityPort,
        ILogger logger,
        CancellationToken ct)
    {
        NyxIdAuthorizationCatalogRefreshResult catalogRefresh;
        if (catalogRefreshLifecycle is null)
        {
            catalogRefresh = new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Unspecified,
                "nyxid_catalog_refresh_not_configured");
        }
        else
        {
            try
            {
                catalogRefresh = await catalogRefreshLifecycle
                    .RefreshPersonalAsync(user.Sub, exchange.AccessToken!, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "NyxID login completed while authorization catalog refresh failed; catalog readiness is degraded.");
                catalogRefresh = new NyxIdAuthorizationCatalogRefreshResult(
                    NyxIdAuthorizationCatalogRefreshStatus.Failed,
                    "nyxid_catalog_refresh_failed");
            }
        }

        var visibility = new NyxIdAuthorizationCatalogVisibilityResult(
            NyxIdAuthorizationCatalogVisibilityStatus.Unspecified,
            0,
            0,
            string.Empty);
        if (catalogRefresh.Success)
        {
            if (catalogVisibilityPort == null)
            {
                visibility = NyxIdAuthorizationCatalogVisibilityResult.Unavailable(catalogRefresh.StateVersion);
            }
            else
            {
                try
                {
                    visibility = await catalogVisibilityPort.ResolveAsync(
                            PersonalCatalogOwner(user.Sub),
                            catalogRefresh.StateVersion,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "NyxID login completed while authorization catalog visibility could not be read.");
                    visibility = NyxIdAuthorizationCatalogVisibilityResult.Unavailable(catalogRefresh.StateVersion);
                }
            }
        }

        return Results.Ok(BuildResponse(
            exchange,
            user,
            bindingDispatchAccepted,
            catalogRefresh,
            visibility));
    }

    private enum ExistingBindingProbeResult
    {
        Usable,
        Stale,
        Unavailable,
    }

    private enum IssuedBindingProbeResult
    {
        Usable,
        MissingRequiredAccess,
        Invalid,
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
        catch (BindingServiceAccessMismatchException ex)
        {
            logger.LogInformation(ex, "NyxID owner binding lacks required service access for {Platform}:{Tenant}:{User}; refreshing local binding.",
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

    private static async Task<IssuedBindingProbeResult> ProbeIssuedBindingAsync(
        INyxIdCapabilityBroker capabilityBroker,
        ExternalSubjectRef subject,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await capabilityBroker
                .IssueShortLivedByBindingIdAsync(
                    subject,
                    bindingId,
                    new CapabilityScope { Value = AevatarOAuthClientScopes.Proxy },
                    ct)
                .ConfigureAwait(false);
            return IssuedBindingProbeResult.Usable;
        }
        catch (BindingScopeMismatchException ex)
        {
            logger.LogInformation(ex, "New NyxID binding lacks the required proxy scope.");
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingServiceAccessMismatchException ex)
        {
            logger.LogInformation(ex, "New NyxID binding lacks one or more required services.");
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingRevokedException ex)
        {
            logger.LogWarning(ex, "New NyxID binding was already revoked before adoption.");
            return IssuedBindingProbeResult.Invalid;
        }
        catch (BindingNotFoundException ex)
        {
            logger.LogWarning(ex, "New NyxID binding was not found before adoption.");
            return IssuedBindingProbeResult.Invalid;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "New NyxID binding could not be verified before adoption.");
            return IssuedBindingProbeResult.Unavailable;
        }
    }

    private static IResult BuildIssuedBindingProbeError(IssuedBindingProbeResult probeResult) =>
        probeResult switch
        {
            IssuedBindingProbeResult.MissingRequiredAccess => LoginFinalizationProblem(
                StatusCodes.Status409Conflict,
                "required_service_access_missing",
                "Return to NyxID and keep every service marked as required by Aevatar selected."),
            IssuedBindingProbeResult.Invalid => LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "issued_binding_invalid",
                "NyxID issued a binding that was unavailable before Aevatar could adopt it."),
            _ => LoginFinalizationProblem(
                StatusCodes.Status503ServiceUnavailable,
                "issued_binding_probe_failed",
                "The new NyxID service grant could not be verified; retry later."),
        };

    // Login failure branches must never answer with 502/504: Cloudflare replaces
    // origin-generated 502/504 responses with its own opaque error page, which
    // strips this structured body before it reaches the client (2026-07-28 login
    // incident). Upstream faults are reported as 503 with a stable error code.
    private static IResult LoginFinalizationProblem(int statusCode, string errorCode, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["error"] = errorCode });

    private static async Task<BindingDispatchOutcome> DispatchCommitBindingAsync(
        ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        ExternalSubjectRef subject,
        string bindingId,
        string ownerScopeId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var accepted = await bindingDispatch.DispatchAsync(new CommitBindingCommand
            {
                ExternalSubject = subject,
                BindingId = bindingId.Trim(),
                OwnerScopeId = ownerScopeId.Trim(),
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

    private static async Task<BindingDispatchOutcome> DispatchReplaceBindingAsync(
        ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingReplaceDispatch,
        ExternalSubjectRef subject,
        string expectedPreviousBindingId,
        string bindingId,
        string ownerScopeId,
        string reason,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var accepted = await bindingReplaceDispatch.DispatchAsync(new ReplaceBindingCommand
            {
                ExternalSubject = subject,
                BindingId = bindingId.Trim(),
                ExpectedPreviousBindingId = expectedPreviousBindingId.Trim(),
                OwnerScopeId = ownerScopeId.Trim(),
                Reason = reason,
            }, ct).ConfigureAwait(false);

            if (accepted.Succeeded && accepted.Receipt != null)
                return BindingDispatchOutcome.Accepted;

            logger.LogError("NyxID login finalization binding replacement dispatch rejected: error={Error}.", accepted.Error);
            return BindingDispatchOutcome.Rejected;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID login finalization failed to dispatch ReplaceBindingCommand for {Platform}:{Tenant}:{User}.",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return BindingDispatchOutcome.Failed;
        }
    }

    private static NyxIdLoginFinalizationResponse BuildResponse(
        BrokerAuthorizationCodeResult exchange,
        NyxIdFinalizedUserInfo user,
        bool bindingDispatchAccepted,
        NyxIdAuthorizationCatalogRefreshResult catalogRefresh,
        NyxIdAuthorizationCatalogVisibilityResult catalogVisibility) =>
        new(
            Tokens: new NyxIdFinalizedTokenSet(
                AccessToken: exchange.AccessToken ?? string.Empty,
                RefreshToken: exchange.RefreshToken,
                TokenType: string.IsNullOrWhiteSpace(exchange.TokenType) ? "Bearer" : exchange.TokenType,
                ExpiresIn: exchange.ExpiresIn ?? 3600,
                IdToken: exchange.IdToken,
                Scope: exchange.Scope),
            User: user,
            BindingDispatchAccepted: bindingDispatchAccepted,
            AuthorizationCatalogReady: catalogVisibility.Ready,
            AuthorizationCatalogRefreshStatus: ToCatalogRefreshStatus(catalogRefresh.Status),
            AuthorizationCatalogRefreshFailureCode: catalogRefresh.FailureCode,
            AuthorizationCatalogVisibilityStatus: ToCatalogVisibilityStatus(catalogVisibility.Status),
            AuthorizationCatalogVisibilityFailureCode: catalogVisibility.FailureCode,
            AuthorizationCatalogRequiredStateVersion: catalogVisibility.RequiredStateVersion,
            AuthorizationCatalogVisibleStateVersion: catalogVisibility.VisibleStateVersion);

    private static AuthorizationOwnerIdentity PersonalCatalogOwner(string ownerSubject) => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = ownerSubject.Trim(),
    };

    private static string ToCatalogRefreshStatus(NyxIdAuthorizationCatalogRefreshStatus status) => status switch
    {
        NyxIdAuthorizationCatalogRefreshStatus.Observed => "observed",
        NyxIdAuthorizationCatalogRefreshStatus.AccessDenied => "access_denied",
        NyxIdAuthorizationCatalogRefreshStatus.Failed => "failed",
        NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut => "observation_timed_out",
        NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported => "owner_not_supported",
        NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable => "catalog_unstable",
        NyxIdAuthorizationCatalogRefreshStatus.Superseded => "superseded",
        _ => "not_configured",
    };

    private static string ToCatalogVisibilityStatus(NyxIdAuthorizationCatalogVisibilityStatus status) => status switch
    {
        NyxIdAuthorizationCatalogVisibilityStatus.Ready => "ready",
        NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending => "projection_pending",
        NyxIdAuthorizationCatalogVisibilityStatus.OwnerMismatch => "owner_mismatch",
        NyxIdAuthorizationCatalogVisibilityStatus.Invalidated => "invalidated",
        NyxIdAuthorizationCatalogVisibilityStatus.Stale => "stale",
        NyxIdAuthorizationCatalogVisibilityStatus.Invalid => "invalid",
        NyxIdAuthorizationCatalogVisibilityStatus.Unavailable => "unavailable",
        _ => "not_evaluated",
    };


    private static string? ResolveRequestBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault()?.Trim();
        const string prefix = "Bearer ";
        if (header == null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 0 || token.Contains(',') ? null : token;
    }

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

public sealed record NyxIdAuthorizationCatalogRefreshResponse(
    bool Ready,
    string RefreshStatus,
    string RefreshFailureCode,
    string VisibilityStatus,
    string VisibilityFailureCode,
    long RequiredStateVersion = 0,
    long VisibleStateVersion = 0);

public sealed record NyxIdLoginFinalizationRequest
{
    public string? Code { get; init; }
    public string? CodeVerifier { get; init; }
    public string? RedirectUri { get; init; }
    public bool ServiceAccessReview { get; init; }
}

public sealed record NyxIdLoginFinalizationResponse(
    NyxIdFinalizedTokenSet Tokens,
    NyxIdFinalizedUserInfo User,
    bool BindingDispatchAccepted,
    bool AuthorizationCatalogReady,
    string AuthorizationCatalogRefreshStatus,
    string AuthorizationCatalogRefreshFailureCode,
    string AuthorizationCatalogVisibilityStatus,
    string AuthorizationCatalogVisibilityFailureCode,
    long AuthorizationCatalogRequiredStateVersion = 0,
    long AuthorizationCatalogVisibleStateVersion = 0);

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
