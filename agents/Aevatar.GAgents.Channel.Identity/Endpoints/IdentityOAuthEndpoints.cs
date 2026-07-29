using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.Channel.Identity.Endpoints;

/// <summary>
/// HTTP endpoints owned by the Channel.Identity module. See
/// <c>MapIdentityOAuthEndpoints</c> for the route table.
/// </summary>
public static class IdentityOAuthEndpoints
{
    private const int MaxWebhookBodyBytes = 64 * 1024;
    private const string OAuthClientStatusUrl = "/api/oauth/aevatar-client/status";

    public static IEndpointRouteBuilder MapIdentityOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/oauth/nyxid-callback", HandleNyxIdOAuthCallbackAsync)
            .WithTags("ChannelIdentity")
            .WithEndpointAudit(
                "identity.oauth.callback",
                AuditSensitivityLevel.Confidential,
                "external_identity_binding",
                EndpointAuditTargetResolvers.Static("external_identity_binding", "callback"),
                captureUnauthenticated: true)
            .AllowAnonymous();
        app.MapPost("/api/webhooks/nyxid-broker-revocation", HandleBrokerRevocationWebhookAsync)
            .WithTags("ChannelIdentity")
            .WithEndpointAudit(
                "identity.binding.broker-revocation",
                AuditSensitivityLevel.Restricted,
                "external_identity_binding",
                EndpointAuditTargetResolvers.Static("external_identity_binding", "broker-revocation"),
                captureUnauthenticated: true)
            .AllowAnonymous();
        app.MapGet("/api/oauth/aevatar-client/status", HandleAevatarOAuthClientStatusAsync)
            .WithTags("ChannelIdentity")
            .AllowAnonymous();
        // Operator-only: reconcile the cluster-singleton OAuth client snapshot
        // from deployment configuration. Aevatar admin policy is checked inline
        // because this module does not own an ASP.NET auth scheme.
        app.MapPost("/api/oauth/aevatar-client/rebuild", HandleAevatarOAuthClientRebuildAsync)
            .WithTags("ChannelIdentity")
            .WithEndpointAudit(
                "identity.oauth-client.rebuild",
                AuditSensitivityLevel.Restricted,
                "aevatar_oauth_client",
                EndpointAuditTargetResolvers.Static("aevatar_oauth_client", "rebuild"),
                captureUnauthenticated: true)
            .AddEndpointFilter<RebuildAuthEndpointFilter>()
            .AllowAnonymous();
        // Operator-only: force a fresh HMAC state-token signing key. Recovery
        // path when the vault entry behind the persisted key reference is lost
        // (secret store data loss): rotation writes new key material and its
        // committed event re-materializes the readmodel. Same admin gate as
        // the client rebuild.
        app.MapPost("/api/oauth/aevatar-client/rotate-hmac", HandleAevatarOAuthClientRotateHmacAsync)
            .WithTags("ChannelIdentity")
            .WithEndpointAudit(
                "identity.oauth-client.hmac-rotate",
                AuditSensitivityLevel.Restricted,
                "aevatar_oauth_client",
                EndpointAuditTargetResolvers.Static("aevatar_oauth_client", "hmac-rotate"),
                captureUnauthenticated: true)
            .AddEndpointFilter<RebuildAuthEndpointFilter>()
            .AllowAnonymous();
        // Operator-only: rebuild a wiped/reset current-state readmodel for one NyxID
        // owner binding from the surviving actor state — headless disaster recovery,
        // no browser round-trip. Same admin gate as the client rebuild.
        app.MapPost("/api/oauth/nyxid-binding/rebuild", HandleNyxIdBindingRebuildAsync)
            .WithTags("ChannelIdentity")
            .WithEndpointAudit(
                "identity.binding.rebuild",
                AuditSensitivityLevel.Restricted,
                "external_identity_binding",
                EndpointAuditTargetResolvers.Static("external_identity_binding", "rebuild"),
                captureUnauthenticated: true)
            .AddEndpointFilter<RebuildAuthEndpointFilter>()
            .AllowAnonymous();

        return app;
    }

    // ─── OAuth callback ───

    internal static async Task<IResult> HandleNyxIdOAuthCallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery] string? format,
        [FromServices] INyxIdBrokerCallbackClient brokerCallback,
        [FromServices] INyxIdCapabilityBroker capabilityBroker,
        [FromServices] IExternalIdentityBindingQueryPort queryPort,
        [FromServices] ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        [FromServices] ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingReplaceDispatch,
        [FromServices] IOwnerScopeResolver ownerScopeResolver,
        [FromServices] ICommandDispatchService<ObserveBrokerCapabilityCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> brokerCapabilityDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.OAuthCallback");

        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.LogWarning("OAuth callback received error from NyxID: {Error}", error);
            return Results.BadRequest(new { error, detail = "NyxID returned an error on the OAuth callback. Re-run /init from Lark to retry." });
        }
        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new { error = "code_missing" });
        if (string.IsNullOrWhiteSpace(state))
            return Results.BadRequest(new { error = "state_missing" });

        var decode = await brokerCallback.TryDecodeStateTokenAsync(state, ct).ConfigureAwait(false);
        if (!decode.Succeeded || decode.ExternalSubject is null)
        {
            logger.LogWarning("OAuth callback rejected state token: {ErrorCode}", decode.ErrorCode);
            // Cluster cold-start: same root cause as /init's "正在初始化"
            // hint — the verifier silo lost the cached snapshot or the
            // bootstrap actor isn't ready yet. Surface a specific message so
            // the user retries instead of suspecting a tampered link.
            var detail = decode.ErrorCode == "state_client_not_provisioned"
                ? "Aevatar 集群正在初始化 NyxID 客户端,请 30 秒后回到 Lark 重新发送 /init。"
                : "绑定链接已过期或无效,请回到 Lark 重新发送 /init";
            return Results.BadRequest(new
            {
                error = decode.ErrorCode,
                detail,
            });
        }
        var subject = decode.ExternalSubject;
        var verifier = decode.PkceVerifier ?? string.Empty;

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await brokerCallback.ExchangeAuthorizationCodeAsync(code, verifier, ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException ex)
        {
            // The broker now refuses to exchange a code when the snapshot's
            // redirect_uri doesn't match the resolver's output (drift state
            // protection added in this PR). This is the same "still
            // initializing / drift not yet healed" condition the state-token
            // decoder surfaces above, so route it to the same retry-friendly
            // 400 path instead of letting the generic catch return 503
            // token_exchange_failed — that misclassifies a self-recoverable
            // condition as a NyxID outage.
            logger.LogWarning(
                ex,
                "OAuth callback rejected because the OAuth client snapshot is missing or drifted; bootstrap is still healing. correlation={CorrelationId}",
                decode.CorrelationId);
            return Results.BadRequest(new
            {
                error = "client_not_provisioned",
                detail = "Aevatar 集群正在初始化 NyxID 客户端,请 30 秒后回到 Lark 重新发送 /init。",
            });
        }
        catch (NyxIdRequiredServiceAccessException ex)
        {
            logger.LogInformation(
                ex,
                "OAuth callback rejected because the user did not grant every required NyxID service. correlation={CorrelationId}",
                decode.CorrelationId);
            return Results.Json(new
            {
                error = "required_service_access_missing",
                detail = "NyxID 授权未包含 Aevatar、默认 LLM、Ornn service 或 Sandbox service。请回到 Lark 重新发送 /init,并在授权页保留这些必需 services。",
            }, statusCode: StatusCodes.Status409Conflict);
        }
        // RFC 6749 §5.2: the token endpoint answers 400 for a bad grant
        // (expired/replayed code) — a user-recoverable condition, not an
        // upstream outage.
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            logger.LogWarning(ex, "NyxID rejected the OAuth callback authorization code for correlation {CorrelationId}", decode.CorrelationId);
            return OAuthCallbackProblem(
                StatusCodes.Status400BadRequest,
                "authorization_code_rejected",
                "NyxID 拒绝了本次授权码,绑定链接可能已过期。请回到 Lark 重新发送 /init。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback authorization-code exchange failed for correlation {CorrelationId}", decode.CorrelationId);
            return OAuthCallbackProblem(
                StatusCodes.Status503ServiceUnavailable,
                "token_exchange_failed",
                "NyxID 绑定失败,稍后重试 /init");
        }

        var existingBinding = await queryPort.ResolveAsync(subject, ct).ConfigureAwait(false);
        if (exchange.BindingUpdated)
        {
            var expectedBindingHash = decode.ExpectedBindingHash?.Trim() ?? string.Empty;
            var currentBindingHash = existingBinding is null
                ? string.Empty
                : NyxIdRemoteCapabilityBroker.HashBindingId(existingBinding.Value);
            if (expectedBindingHash.Length == 0
                || !string.Equals(expectedBindingHash, currentBindingHash, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "OAuth binding-grant update completed for a stale local binding reference. correlation={CorrelationId}, expected_hash={ExpectedHash}, current_hash={CurrentHash}",
                    decode.CorrelationId,
                    expectedBindingHash,
                    currentBindingHash);
                return Results.Json(new
                {
                    error = "binding_changed_during_review",
                    detail = "Lark 中的 NyxID 绑定在授权期间发生了变化。请回到 Lark 重新发送 /init。",
                }, statusCode: StatusCodes.Status409Conflict);
            }

            var updatedBindingProbe = await ProbeIssuedBindingAsync(
                    capabilityBroker,
                    subject,
                    existingBinding!.Value,
                    logger,
                    ct)
                .ConfigureAwait(false);
            if (updatedBindingProbe != IssuedBindingProbeResult.Usable)
                return BuildIssuedBindingProbeError(updatedBindingProbe);

            logger.LogInformation(
                "Updated NyxID service grant in place for {Platform}:{Tenant}:{User}; binding_id remained unchanged",
                subject.Platform,
                subject.Tenant,
                subject.ExternalUserId);
            return RenderBindingGrantUpdated(format);
        }

        // Defensive: NyxID returned no binding_id even though authorization-code
        // exchange succeeded. Post NyxID#576 fix, broker mode is triggered by
        // EITHER `broker_capability_enabled=true` OR `urn:nyxid:scope:broker_binding`
        // appearing in the client's `allowed_scopes` (oauth_broker_service.rs
        // is_broker_client). Aevatar's configured public client must allow that
        // scope, so the happy path returns a binding_id automatically. Reaching
        // this branch means the configured client registration is incomplete.
        if (string.IsNullOrEmpty(exchange.BindingId))
        {
            logger.LogWarning(
                "OAuth callback succeeded but NyxID did not return a binding_id — the configured OAuth client is registered without broker capability. Expected `urn:nyxid:scope:broker_binding` in allowed_scopes or `broker_capability_enabled=true`.");
            return Results.Json(new
            {
                status = "broker_capability_disabled",
                detail = "Aevatar 配置的 OAuth client 未授予 broker capability。请检查 /api/oauth/aevatar-client/status 显示的 client_id 是否与 NyxID registration 一致,并在 NyxID admin 中为该 client 授予 broker_binding/proxy scope 后重试 /init。",
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var ownerScopeId = ResolveOwnerScopeId(exchange.IdToken);
        if (string.IsNullOrWhiteSpace(ownerScopeId))
        {
            logger.LogWarning(
                "OAuth callback succeeded but id_token did not carry a stable NyxID uid/sub claim. correlation={CorrelationId}",
                decode.CorrelationId);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return OAuthCallbackProblem(
                StatusCodes.Status503ServiceUnavailable,
                "owner_scope_missing",
                "NyxID binding succeeded but Aevatar could not resolve the canonical owner scope. Re-run /init later.");
        }

        var stateExpectedBindingHash = decode.ExpectedBindingHash?.Trim() ?? string.Empty;
        var projectedBindingHash = existingBinding is null
            ? string.Empty
            : NyxIdRemoteCapabilityBroker.HashBindingId(existingBinding.Value);
        if (!string.Equals(stateExpectedBindingHash, projectedBindingHash, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "OAuth callback received a new binding for a stale local binding reference. correlation={CorrelationId}",
                decode.CorrelationId);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "binding_changed_during_review",
                detail = "Lark 中的 NyxID 绑定在授权期间发生了变化。请回到 Lark 重新发送 /init。",
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var actorId = subject.ToActorId();
        var replacingExistingBinding = existingBinding is not null;
        if (replacingExistingBinding)
        {
            OwnerScopeId? existingOwnerScope;
            try
            {
                existingOwnerScope = await ownerScopeResolver.ResolveAsync(subject, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "OAuth callback could not resolve the current binding owner for actor={ActorId}",
                    actorId);
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Json(new
                {
                    error = "binding_owner_lookup_failed",
                    detail = "Aevatar 暂时无法核对当前 NyxID 账号。请稍后回到 Lark 重新发送 /init。",
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(existingOwnerScope?.Value))
            {
                try
                {
                    existingOwnerScope = await brokerCallback
                        .ResolveBindingOwnerScopeAsync(existingBinding!.Value, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "OAuth callback could not introspect the legacy binding owner for actor={ActorId}",
                        actorId);
                    await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                    return Results.Json(new
                    {
                        error = "binding_owner_lookup_failed",
                        detail = "Aevatar 暂时无法核对当前 NyxID 账号。请稍后回到 Lark 重新发送 /init。",
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var existingOwnerScopeId = existingOwnerScope?.Value?.Trim() ?? string.Empty;
            if (existingOwnerScopeId.Length == 0)
            {
                logger.LogWarning(
                    "OAuth callback cannot safely replace a binding without a materialized owner scope. actor={ActorId}, correlation={CorrelationId}",
                    actorId,
                    decode.CorrelationId);
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Json(new
                {
                    error = "binding_owner_missing",
                    detail = "当前绑定缺少可验证的 NyxID 账号归属。请先在 Lark 发送 /unbind，再发送 /init 重新绑定。",
                }, statusCode: StatusCodes.Status409Conflict);
            }

            if (!string.Equals(existingOwnerScopeId, ownerScopeId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "OAuth callback rejected a silent NyxID account switch for actor={ActorId}, correlation={CorrelationId}",
                    actorId,
                    decode.CorrelationId);
                await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
                return Results.Json(new
                {
                    error = "binding_owner_mismatch",
                    detail = "当前 Lark 身份已绑定另一个 NyxID 账号。如需切换账号，请先在 Lark 发送 /unbind，再发送 /init。",
                }, statusCode: StatusCodes.Status409Conflict);
            }
        }

        var issuedBindingProbe = await ProbeIssuedBindingAsync(
                capabilityBroker,
                subject,
                exchange.BindingId,
                logger,
                ct)
            .ConfigureAwait(false);
        if (issuedBindingProbe != IssuedBindingProbeResult.Usable)
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return BuildIssuedBindingProbeError(issuedBindingProbe);
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            accepted = replacingExistingBinding
                ? await bindingReplaceDispatch
                    .DispatchAsync(new ReplaceBindingCommand
                    {
                        ExternalSubject = subject.Clone(),
                        BindingId = exchange.BindingId,
                        ExpectedPreviousBindingId = existingBinding!.Value,
                        OwnerScopeId = ownerScopeId,
                        Reason = "channel_service_access_review",
                    }, ct)
                    .ConfigureAwait(false)
                : await bindingDispatch
                    .DispatchAsync(new CommitBindingCommand
                    {
                        ExternalSubject = subject.Clone(),
                        BindingId = exchange.BindingId,
                        OwnerScopeId = ownerScopeId,
                    }, ct)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "OAuth callback failed to dispatch the binding {Operation} command for actor={ActorId}",
                replacingExistingBinding ? "replacement" : "commit",
                actorId);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "NyxID 绑定请求未能进入本地处理队列,请稍后重试 /init",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!accepted.Succeeded || accepted.Receipt is null)
        {
            logger.LogError(
                "OAuth callback binding {Operation} dispatch rejected for actor={ActorId}: error={Error}",
                replacingExistingBinding ? "replacement" : "commit",
                actorId,
                accepted.Error);
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "NyxID 绑定请求未被本地处理队列接受,请稍后重试 /init",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Observe broker capability on the cluster client (idempotent) — first
        // successful binding_id is proof that NyxID admin enabled the flag.
        try
        {
            await brokerCapabilityDispatch
                .DispatchAsync(new ObserveBrokerCapabilityCommand(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record broker capability observation; continuing");
        }

        var displayName = ResolveDisplayName(exchange.IdToken);
        logger.LogInformation(
            "Accepted external identity binding {Operation} dispatch for {Platform}:{Tenant}:{User}, command_id={CommandId}",
            replacingExistingBinding ? "replacement" : "commit",
            subject.Platform,
            subject.Tenant,
            subject.ExternalUserId,
            accepted.Receipt.CommandId);

        return RenderBindingAccepted(displayName, accepted.Receipt, format);
    }

    private enum IssuedBindingProbeResult
    {
        Usable,
        MissingRequiredAccess,
        Invalid,
        Unavailable,
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
            logger.LogInformation(ex, "New channel NyxID binding lacks the required proxy scope.");
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingServiceAccessMismatchException ex)
        {
            logger.LogInformation(ex, "New channel NyxID binding lacks one or more required services.");
            return IssuedBindingProbeResult.MissingRequiredAccess;
        }
        catch (BindingRevokedException ex)
        {
            logger.LogWarning(ex, "New channel NyxID binding was already revoked before adoption.");
            return IssuedBindingProbeResult.Invalid;
        }
        catch (BindingNotFoundException ex)
        {
            logger.LogWarning(ex, "New channel NyxID binding was not found before adoption.");
            return IssuedBindingProbeResult.Invalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "New channel NyxID binding could not be verified before adoption.");
            return IssuedBindingProbeResult.Unavailable;
        }
    }

    private static IResult BuildIssuedBindingProbeError(IssuedBindingProbeResult probeResult) =>
        probeResult switch
        {
            IssuedBindingProbeResult.MissingRequiredAccess => OAuthCallbackProblem(
                StatusCodes.Status409Conflict,
                "required_service_access_missing",
                "NyxID 授权没有覆盖 Aevatar 所需的 scope 或 services。请回到 Lark 重新发送 /init，并在授权页保留所有必需 services。"),
            IssuedBindingProbeResult.Invalid => OAuthCallbackProblem(
                StatusCodes.Status503ServiceUnavailable,
                "issued_binding_invalid",
                "NyxID 新授权在 Aevatar 接管前已失效。请回到 Lark 重新发送 /init。"),
            _ => OAuthCallbackProblem(
                StatusCodes.Status503ServiceUnavailable,
                "issued_binding_probe_failed",
                "Aevatar 暂时无法验证新的 NyxID 服务授权。请稍后回到 Lark 重新发送 /init。"),
        };

    // Callback failure branches must never answer with 502/504: Cloudflare
    // replaces origin-generated 502/504 responses with its own opaque branded
    // error page, which strips this structured body before it reaches the
    // client (2026-07-28 login incident). Upstream faults are reported as 503
    // with a stable error code.
    private static IResult OAuthCallbackProblem(int statusCode, string errorCode, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["error"] = errorCode });

    // ─── Status endpoint ───

    internal static async Task<IResult> HandleAevatarOAuthClientStatusAsync(
        [FromServices] IAevatarOAuthClientProvider provider,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await provider.GetAsync(ct).ConfigureAwait(false);
            var resolvedRedirectUri = NyxIdRedirectUriResolver.Resolve();
            var resolvedRedirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris();
            var registeredRedirectUris = NyxIdRedirectUriResolver.NormalizeRedirectUris(snapshot.RedirectUris ?? []);
            var redirectUriDrifted = string.IsNullOrEmpty(snapshot.RedirectUri)
                || !string.Equals(snapshot.RedirectUri, resolvedRedirectUri, StringComparison.Ordinal);
            var redirectUriListDrifted = registeredRedirectUris.Count == 0
                || registeredRedirectUris.Count != resolvedRedirectUris.Count
                || !registeredRedirectUris.SequenceEqual(resolvedRedirectUris, StringComparer.Ordinal);
            var oauthScopeDrifted =
                !AevatarOAuthClientScopes.ContainsRequiredAuthorizationScopes(snapshot.OauthScope);
            var status = redirectUriDrifted || redirectUriListDrifted
                ? "redirect_uri_drifted"
                : oauthScopeDrifted ? "oauth_scope_drifted"
                : snapshot.BrokerCapabilityObserved ? "ready" : "broker_capability_pending";
            return Results.Ok(new
            {
                status,
                client_id = snapshot.ClientId,
                client_id_issued_at = snapshot.ClientIdIssuedAt,
                nyxid_authority = snapshot.NyxIdAuthority,
                redirect_uri_registered = snapshot.RedirectUri,
                redirect_uri_resolved = resolvedRedirectUri,
                redirect_uri_drifted = redirectUriDrifted,
                redirect_uris_registered = registeredRedirectUris,
                redirect_uris_resolved = resolvedRedirectUris,
                redirect_uris_drifted = redirectUriListDrifted,
                oauth_scope_registered = snapshot.OauthScope,
                oauth_scope_required = AevatarOAuthClientScopes.AuthorizationScope,
                oauth_scope_drifted = oauthScopeDrifted,
                broker_capability_observed = snapshot.BrokerCapabilityObserved,
                broker_capability_observed_at = snapshot.BrokerCapabilityObservedAt,
                ops_handoff = oauthScopeDrifted
                    ? "The configured OAuth client registration must include the canonical proxy-capable scope."
                    : snapshot.BrokerCapabilityObserved
                        ? null
                        : "Operator must enable broker_capability_enabled on this OAuth client at NyxID admin (one-time per cluster).",
            });
        }
        catch (AevatarOAuthClientNotProvisionedException)
        {
            return Results.Json(new
            {
                status = "not_provisioned",
                detail = $"OAuth client configuration or actor materialization is unavailable. Check '{AevatarOAuthClientOptions.ClientIdConfigurationKey}' and host startup logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // ─── Operator rebuild ───

    /// <summary>
    /// Reconciles the actor snapshot from the configured client id and the
    /// canonical redirect/scope contract. The request cannot supply identity
    /// fields, so this repair path cannot become a second client-id authority.
    /// </summary>
    internal static Task<IResult> HandleAevatarOAuthClientRebuildAsync(
        HttpContext http,
        [FromServices] IOptions<AevatarOAuthClientOptions> clientOptions,
        [FromServices] ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        [FromServices] ICommandDispatchService<RebuildAevatarOAuthClientProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> projectionRebuildDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        HandleAevatarOAuthClientRebuildCoreAsync(
            http,
            clientOptions.Value,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            rebuildDispatch,
            projectionRebuildDispatch,
            loggerFactory,
            ct);

    /// <summary>
    /// Core method exposed for tests to pass the admin authorizer and the typed dispatch service directly, without resolving
    /// endpoint-bound services.
    /// </summary>
    internal static async Task<IResult> HandleAevatarOAuthClientRebuildCoreAsync(
        HttpContext http,
        AevatarOAuthClientOptions clientOptions,
        IPlatformAdminAuthorizer? adminAuthorizer,
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        ICommandDispatchService<RebuildAevatarOAuthClientProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> projectionRebuildDispatch,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.OAuthRebuild");

        var authorization = await AuthorizeRebuildAsync(http, adminAuthorizer, logger, ct)
            .ConfigureAwait(false);
        if (authorization.Rejection is not null)
            return authorization.Rejection;

        if (string.IsNullOrWhiteSpace(clientOptions.ClientId))
        {
            return Results.Json(new
            {
                error = "oauth_client_id_not_configured",
                detail = $"Configure a non-empty '{AevatarOAuthClientOptions.ClientIdConfigurationKey}' before reconciling the OAuth client actor.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var authority = NyxIdAuthorityResolver.Resolve(logger);
        var redirectUri = NyxIdRedirectUriResolver.Resolve(logger);
        var redirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris(logger);
        var oauthScope = AevatarOAuthClientScopes.AuthorizationScope;

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            var command = new ProvisionAevatarOAuthClientCommand
            {
                ClientId = clientOptions.ClientId.Trim(),
                ClientIdIssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                NyxidAuthority = authority,
                OauthScope = oauthScope,
                RedirectUri = redirectUri,
            };
            command.RedirectUris.AddRange(redirectUris);
            accepted = await rebuildDispatch
                .DispatchAsync(command, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rebuild endpoint failed to dispatch ProvisionAevatarOAuthClientCommand.");
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "Failed to dispatch the provision command to the OAuth client actor. Check silo logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!accepted.Succeeded || accepted.Receipt is null)
        {
            logger.LogError("Rebuild endpoint dispatch rejected: error={Error}", accepted.Error);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "Provision command was rejected before entering the OAuth client actor inbox.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Reconciliation is a no-op when the surviving actor state already
        // matches deployment configuration, so a wiped projection store would
        // stay empty forever. The explicit projection-rebuild command re-emits
        // the current committed state without appending an event.
        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> projectionAccepted;
        try
        {
            projectionAccepted = await projectionRebuildDispatch
                .DispatchAsync(new RebuildAevatarOAuthClientProjectionCommand(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rebuild endpoint failed to dispatch RebuildAevatarOAuthClientProjectionCommand.");
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "Provision reconciliation was accepted, but the projection rebuild command could not be dispatched. Check silo logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!projectionAccepted.Succeeded || projectionAccepted.Receipt is null)
        {
            logger.LogError("Rebuild endpoint projection-rebuild dispatch rejected: error={Error}", projectionAccepted.Error);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "Provision reconciliation was accepted, but the projection rebuild command was rejected before entering the OAuth client actor inbox.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogWarning(
            "Operator rebuild accepted for AevatarOAuthClientGAgent: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, command_id={CommandId}, projection_rebuild_command_id={ProjectionRebuildCommandId}, admin_user_id={AdminUserId}, admin_email={AdminEmail}, admin_grant_source={GrantSource}.",
            clientOptions.ClientId,
            authority,
            redirectUri,
            accepted.Receipt.CommandId,
            projectionAccepted.Receipt.CommandId,
            authorization.Caller.UserId,
            authorization.Caller.Email,
            authorization.Caller.GrantSource);

        return Results.Accepted(OAuthClientStatusUrl, new
        {
            status = "rebuild_pending",
            command_id = accepted.Receipt.CommandId,
            correlation_id = accepted.Receipt.CorrelationId,
            actor_id = accepted.Receipt.ActorId,
            projection_rebuild_command_id = projectionAccepted.Receipt.CommandId,
            status_url = OAuthClientStatusUrl,
            admin_grant_source = authorization.Caller.GrantSource,
            detail = "Configured client reconciliation accepted for dispatch. Re-poll the status URL until actor state and projection materialize.",
        });
    }

    // ─── Operator HMAC key rotation (disaster recovery) ───

    /// <summary>
    /// Forces a fresh HMAC state-token signing key. Recovery path for a lost
    /// secret-vault entry: rotation writes new key material to the vault and
    /// commits a rotation event, which also re-materializes the readmodel.
    /// Grace window: state tokens signed with the previous key (TTL ≤ 5 min)
    /// keep verifying; in-flight callbacks older than that fail decode.
    /// </summary>
    internal static Task<IResult> HandleAevatarOAuthClientRotateHmacAsync(
        HttpContext http,
        [FromServices] ICommandDispatchService<RotateAevatarOAuthClientHmacKeyCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rotateDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        HandleAevatarOAuthClientRotateHmacCoreAsync(
            http,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            rotateDispatch,
            loggerFactory,
            ct);

    /// <summary>
    /// Core method exposed for tests to pass the admin authorizer and the typed
    /// dispatch service directly, without resolving endpoint-bound services.
    /// </summary>
    internal static async Task<IResult> HandleAevatarOAuthClientRotateHmacCoreAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? adminAuthorizer,
        ICommandDispatchService<RotateAevatarOAuthClientHmacKeyCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rotateDispatch,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.OAuthHmacRotate");

        var authorization = await AuthorizeRebuildAsync(http, adminAuthorizer, logger, ct)
            .ConfigureAwait(false);
        if (authorization.Rejection is not null)
            return authorization.Rejection;

        var idempotencyKeys = http.Request.Headers["Idempotency-Key"];
        var idempotencyKey = idempotencyKeys.ToString().Trim();
        var ifMatch = http.Request.Headers.IfMatch.ToString().Trim();
        var strongIfMatch = ifMatch.Length >= 3 &&
                            ifMatch[0] == '"' &&
                            ifMatch[^1] == '"' &&
                            !ifMatch.AsSpan(1, ifMatch.Length - 2).Contains('"');
        var expectedCurrentKid = strongIfMatch ? ifMatch[1..^1] : string.Empty;
        if (idempotencyKeys.Count != 1 ||
            idempotencyKey.Length is 0 or > 200 ||
            idempotencyKey.Contains(',') ||
            expectedCurrentKid.Length is 0 or > 128 ||
            expectedCurrentKid.Contains(','))
        {
            return Results.BadRequest(new
            {
                error = "rotation_precondition_required",
                detail = "Supply one Idempotency-Key and the expected current kid as If-Match.",
            });
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            accepted = await rotateDispatch
                .DispatchAsync(new RotateAevatarOAuthClientHmacKeyCommand
                {
                    IdempotencyKey = idempotencyKey,
                    ExpectedCurrentKid = expectedCurrentKid,
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rotate endpoint failed to dispatch RotateAevatarOAuthClientHmacKeyCommand.");
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "Failed to dispatch the HMAC rotation command to the OAuth client actor. Check silo logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!accepted.Succeeded || accepted.Receipt is null)
        {
            logger.LogError("Rotate endpoint dispatch rejected: error={Error}", accepted.Error);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "HMAC rotation command was rejected before entering the OAuth client actor inbox.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogWarning(
            "Operator HMAC rotation accepted for AevatarOAuthClientGAgent: command_id={CommandId}, admin_user_id={AdminUserId}, admin_email={AdminEmail}, admin_grant_source={GrantSource}.",
            accepted.Receipt.CommandId,
            authorization.Caller.UserId,
            authorization.Caller.Email,
            authorization.Caller.GrantSource);

        return Results.Accepted(OAuthClientStatusUrl, new
        {
            status = "rotate_pending",
            command_id = accepted.Receipt.CommandId,
            correlation_id = accepted.Receipt.CorrelationId,
            actor_id = accepted.Receipt.ActorId,
            status_url = OAuthClientStatusUrl,
            admin_grant_source = authorization.Caller.GrantSource,
            detail = "HMAC key rotation accepted for dispatch. Re-poll the status URL until the rotated key materializes.",
        });
    }

    // ─── Operator binding-readmodel rebuild (disaster recovery) ───

    /// <summary>
    /// Body for <c>POST /api/oauth/nyxid-binding/rebuild</c>. Identifies the external
    /// subject whose current-state readmodel should be re-materialized from the
    /// surviving actor state. <c>platform</c> defaults to the NyxID owner platform and
    /// <c>tenant</c> to empty — matching how NyxID owner subjects are constructed
    /// elsewhere — so the common case only needs <c>external_user_id</c>.
    /// </summary>
    public sealed record RebuildNyxIdBindingRequest(
        string? external_user_id,
        string? platform,
        string? tenant);

    internal static Task<IResult> HandleNyxIdBindingRebuildAsync(
        HttpContext http,
        [FromBody] RebuildNyxIdBindingRequest? body,
        [FromServices] ICommandDispatchService<RebuildBindingProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        HandleNyxIdBindingRebuildCoreAsync(
            http,
            body,
            http.RequestServices.GetService<IPlatformAdminAuthorizer>(),
            rebuildDispatch,
            loggerFactory,
            ct);

    /// <summary>
    /// Core method exposed for tests to pass the admin authorizer and typed dispatch
    /// service directly, without resolving endpoint-bound services.
    /// </summary>
    internal static async Task<IResult> HandleNyxIdBindingRebuildCoreAsync(
        HttpContext http,
        RebuildNyxIdBindingRequest? body,
        IPlatformAdminAuthorizer? adminAuthorizer,
        ICommandDispatchService<RebuildBindingProjectionCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.BindingRebuild");

        var authorization = await AuthorizeRebuildAsync(http, adminAuthorizer, logger, ct).ConfigureAwait(false);
        if (authorization.Rejection is not null)
            return authorization.Rejection;

        var externalUserId = body?.external_user_id?.Trim();
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            return Results.BadRequest(new
            {
                error = "external_user_id_required",
                detail = "Body must include external_user_id (the NyxID owner subject whose binding readmodel should be rebuilt).",
            });
        }

        var subject = new ExternalSubjectRef
        {
            Platform = string.IsNullOrWhiteSpace(body?.platform)
                ? OwnerScope.NyxIdPlatform
                : body!.platform!.Trim().ToLowerInvariant(),
            Tenant = string.IsNullOrWhiteSpace(body?.tenant) ? string.Empty : body!.tenant!.Trim(),
            ExternalUserId = externalUserId,
        };

        try
        {
            ExternalSubjectRefExtensions.EnsureValid(subject);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_external_subject", detail = ex.Message });
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            accepted = await rebuildDispatch
                .DispatchAsync(new RebuildBindingProjectionCommand { ExternalSubject = subject }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Binding rebuild endpoint failed to dispatch RebuildBindingProjectionCommand for actor={ActorId}.", subject.ToActorId());
            return Results.Json(new
            {
                error = "actor_dispatch_failed",
                detail = "Failed to dispatch the rebuild command to the binding actor. Check silo logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!accepted.Succeeded || accepted.Receipt is null)
        {
            logger.LogError(
                "Binding rebuild endpoint dispatch rejected for actor={ActorId}: error={Error}",
                subject.ToActorId(),
                accepted.Error);
            return Results.Json(new
            {
                error = "actor_dispatch_rejected",
                detail = "Rebuild command was rejected before entering the binding actor inbox.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogWarning(
            "Operator binding-readmodel rebuild accepted: actor_id={ActorId}, command_id={CommandId}, admin_user_id={AdminUserId}, admin_email={AdminEmail}, admin_grant_source={GrantSource}.",
            accepted.Receipt.ActorId,
            accepted.Receipt.CommandId,
            authorization.Caller.UserId,
            authorization.Caller.Email,
            authorization.Caller.GrantSource);

        return Results.Json(new
        {
            status = "rebuild_pending",
            actor_id = accepted.Receipt.ActorId,
            command_id = accepted.Receipt.CommandId,
            correlation_id = accepted.Receipt.CorrelationId,
            admin_grant_source = authorization.Caller.GrantSource,
            detail = "Rebuild command accepted for dispatch. The current-state readmodel re-materializes from the surviving actor state; re-check the binding (e.g. retry the scope-owner schedule) shortly. No-op if the actor holds no active binding.",
        }, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Authorizes a caller for the operator rebuild surface. The caller's bearer
    /// resolves the current user, then aevatar admin policy decides access.
    /// </summary>
    private static async Task<RebuildAuthorization> AuthorizeRebuildAsync(
        HttpContext http,
        IPlatformAdminAuthorizer? adminAuthorizer,
        ILogger logger,
        CancellationToken ct)
    {
        if (adminAuthorizer is null)
        {
            logger.LogWarning("Rebuild endpoint invoked but no aevatar admin authorizer is registered; refusing fail-closed.");
            return new RebuildAuthorization(
                Results.Json(new
                {
                    error = "rebuild_admin_authorizer_unavailable",
                    detail = "Aevatar admin authorization is not configured for OAuth client rebuild.",
                }, statusCode: StatusCodes.Status503ServiceUnavailable),
                PlatformCaller.NotElevated);
        }

        var bearer = ExtractBearerToken(http);
        if (string.IsNullOrWhiteSpace(bearer))
        {
            logger.LogWarning("Rebuild endpoint rejected: missing bearer token.");
            return new RebuildAuthorization(
                Results.Json(
                    new
                    {
                        error = "rebuild_admin_required",
                        detail = "Rebuilding the cluster OAuth client requires aevatar admin access.",
                    },
                    statusCode: StatusCodes.Status403Forbidden),
                PlatformCaller.NotElevated);
        }

        var caller = await adminAuthorizer.ResolveCallerAsync(bearer, ct).ConfigureAwait(false);
        if (!caller.IsElevated)
        {
            logger.LogWarning(
                "Rebuild endpoint rejected: caller lacks aevatar admin access. user_id={UserId}, email={Email}, role={Role}",
                caller.UserId,
                caller.Email,
                caller.Role);
            return new RebuildAuthorization(
                Results.Json(
                    new
                    {
                        error = "rebuild_admin_required",
                        detail = "Rebuilding the cluster OAuth client requires aevatar admin access.",
                    },
                    statusCode: StatusCodes.Status403Forbidden),
                caller);
        }

        return new RebuildAuthorization(null, caller);
    }

    private sealed record RebuildAuthorization(IResult? Rejection, PlatformCaller Caller);

    private static string? ExtractBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[bearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>
    /// Endpoint filter that performs the rebuild authorization check before
    /// model binding and per-request DI activation kick in.
    /// </summary>
    internal sealed class RebuildAuthEndpointFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var adminAuthorizer = http.RequestServices.GetService<IPlatformAdminAuthorizer>();
            var logger = http.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Aevatar.Channel.Identity.OAuthRebuild");

            var authorization = await AuthorizeRebuildAsync(http, adminAuthorizer, logger, http.RequestAborted)
                .ConfigureAwait(false);

            if (authorization.Rejection is not null)
            {
                return authorization.Rejection;
            }

            return await next(context).ConfigureAwait(false);
        }
    }

    // ─── Broker revocation webhook ───

    internal static async Task<IResult> HandleBrokerRevocationWebhookAsync(
        HttpContext http,
        [FromServices] BrokerRevocationWebhookValidator webhookValidator,
        [FromServices] ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> revokeDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.BrokerRevocation");

        byte[] bodyBytes;
        await using (var ms = new MemoryStream())
        {
            var buffer = new byte[8 * 1024];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxWebhookBodyBytes)
                {
                    logger.LogWarning(
                        "Broker revocation webhook body exceeds {MaxBytes} bytes; rejecting",
                        MaxWebhookBodyBytes);
                    return Results.BadRequest(new { error = "body_too_large" });
                }
                await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            bodyBytes = ms.ToArray();
        }

        var validation = await webhookValidator.ValidateAsync(http, bodyBytes, ct).ConfigureAwait(false);
        if (!validation.Succeeded)
        {
            logger.LogWarning("Broker revocation webhook rejected: code={ErrorCode}", validation.ErrorCode);
            return Results.Unauthorized();
        }

        var notification = validation.Notification!;
        if (notification.ExternalSubject is null)
            return Results.BadRequest(new { error = "external_subject_missing" });

        var actorId = notification.ExternalSubject.ToActorId();
        try
        {
            var accepted = await revokeDispatch
                .DispatchAsync(new RevokeBindingCommand
                {
                    ExternalSubject = notification.ExternalSubject.Clone(),
                    Reason = string.IsNullOrWhiteSpace(notification.Reason)
                        ? "nyxid_cae_revocation"
                        : notification.Reason,
                }, ct)
                .ConfigureAwait(false);
            if (!accepted.Succeeded)
                throw new InvalidOperationException($"Broker revocation dispatch rejected: {accepted.Error}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to event-source CAE revocation for actor={ActorId}", actorId);
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Failed to process broker revocation notification.");
        }

        logger.LogInformation(
            "Revoked external identity binding via NyxID CAE: {Platform}:{Tenant}:{User}",
            notification.ExternalSubject.Platform,
            notification.ExternalSubject.Tenant,
            notification.ExternalSubject.ExternalUserId);
        return Results.Accepted();
    }

    private static async Task TryRevokeOrphanBindingAsync(
        INyxIdBrokerCallbackClient brokerCallback,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bindingId))
            return;
        try
        {
            await brokerCallback.RevokeBindingByIdAsync(bindingId, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Revoked orphan binding_id={BindingId} after concurrent /init",
                bindingId);
        }
        catch (Exception ex)
        {
            // Best-effort: leaving an orphan binding at NyxID is preferable
            // to failing the user's already-bound response. NyxID's CAE
            // sweeper eventually reclaims unused bindings.
            logger.LogWarning(ex,
                "Failed to revoke orphan binding_id={BindingId}; NyxID will eventually reap it",
                bindingId);
        }
    }

    private static string? ResolveDisplayName(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
                return name.GetString();
            if (doc.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var raw = sub.GetString();
                return raw is null || raw.Length <= 6 ? raw : raw[..3] + "…" + raw[^3..];
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? ResolveOwnerScopeId(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("uid", out var uid) &&
                uid.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(uid.GetString()))
            {
                return uid.GetString()!.Trim();
            }

            if (doc.RootElement.TryGetProperty("sub", out var sub) &&
                sub.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(sub.GetString()))
            {
                return sub.GetString()!.Trim();
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        return null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Render the user-facing success page returned in the OAuth-callback
    /// response. Issue #513 phase 1 asked for a "callback success → please pick
    /// a model" prompt. The full version is a card update pushed back into
    /// Lark, which requires capturing the /init card's adapter-owned message
    /// id and passing it through the OAuth state token — substantial new
    /// design surface left as a follow-up. This page is the browser-side
    /// substitute the user sees immediately after the OAuth redirect, and it
    /// names the next-step commands (<c>/model</c>, <c>/whoami</c>) explicitly
    /// so the user is not left guessing what to type back in Lark.
    /// </summary>
    /// <remarks>
    /// Display name comes from the id_token "name" / sub claim; HTML-encoded
    /// before interpolation so a malicious id_token cannot inject markup.
    /// Other error paths in the callback intentionally keep returning JSON for
    /// ops/programmatic consumers.
    /// </remarks>
    internal static IResult RenderBoundSuccessHtml(string? displayName, bool alreadyBound) =>
        RenderBoundSuccess(displayName, alreadyBound, format: null);

    /// <summary>
    /// Render the post-binding success response. Default is the HTML browser page that
    /// users land on after clicking the OAuth approve button. Programmatic consumers
    /// (CLI, SDK, integration tests) opt into a JSON envelope by passing
    /// <c>?format=json</c> on the callback URL — the same shape the endpoint returned
    /// before the HTML render landed (PR #570 review #24).
    /// </summary>
    internal static IResult RenderBoundSuccess(string? displayName, bool alreadyBound, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new
            {
                status = "bound",
                already_bound = alreadyBound,
                display_name = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            });
        }

        return RenderBoundSuccessHtmlInternal(displayName, alreadyBound);
    }

    internal static IResult RenderBindingAccepted(
        string? displayName,
        ChannelIdentityOAuthAcceptedReceipt receipt,
        string? format)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new
            {
                status = "binding_pending",
                actor_id = receipt.ActorId,
                command_id = receipt.CommandId,
                correlation_id = receipt.CorrelationId,
                display_name = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                status_url = OAuthClientStatusUrl,
                detail = "Binding command accepted for dispatch. Return to Lark and use /whoami to check once projection materializes.",
            }, statusCode: StatusCodes.Status202Accepted);
        }

        return RenderBindingAcceptedHtmlInternal(displayName, receipt);
    }

    internal static IResult RenderBindingGrantUpdated(string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new
            {
                status = "binding_grant_updated",
                binding_id_changed = false,
            });
        }

        const string html = """
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>NyxID 服务授权 — 已更新</title>
            <style>
            body { font-family: -apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif; max-width: 480px; margin: 60px auto; padding: 0 20px; color: #1d1d1f; line-height: 1.6; }
            .badge { display: inline-block; padding: 4px 10px; background: #d1f5d3; color: #146c2e; border-radius: 999px; font-size: 13px; font-weight: 500; }
            h1 { font-size: 22px; margin: 16px 0 8px; }
            .hint { background: #f5f5f7; padding: 16px 20px; border-radius: 8px; margin-top: 24px; }
            .hint code { background: #fff; padding: 2px 6px; border-radius: 4px; font-family: ui-monospace, "SFMono-Regular", Menlo, monospace; }
            </style>
            </head>
            <body>
            <span class="badge">已更新</span>
            <h1>NyxID 服务授权已更新</h1>
            <p>原有 Lark 绑定保持不变。可以关闭此页并回到 Lark 继续对话。</p>
            <div class="hint">发送 <code>/init</code> 可再次查看服务授权。</div>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    internal static IResult RenderBoundSuccessHtmlInternal(string? displayName, bool alreadyBound)
    {
        var badge = alreadyBound ? "已绑定" : "绑定成功";
        var heading = alreadyBound ? "NyxID 账号已绑定" : "已绑定 NyxID 账号";
        var displayLine = string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : $"<p>账号:{System.Net.WebUtility.HtmlEncode(displayName)}</p>";
        var body = alreadyBound
            ? "<p>当前账号已经完成绑定,无需重复操作。可以关闭此页,回到 Lark 继续对话。</p>"
            : "<p>可以关闭此页,回到 Lark 继续对话。</p>";

        var html = $@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>NyxID 绑定 — {badge}</title>
<style>
body {{ font-family: -apple-system, ""Segoe UI"", ""PingFang SC"", ""Microsoft YaHei"", sans-serif; max-width: 480px; margin: 60px auto; padding: 0 20px; color: #1d1d1f; line-height: 1.6; }}
.badge {{ display: inline-block; padding: 4px 10px; background: #d1f5d3; color: #146c2e; border-radius: 999px; font-size: 13px; font-weight: 500; }}
h1 {{ font-size: 22px; margin: 16px 0 8px; }}
.hint {{ background: #f5f5f7; padding: 16px 20px; border-radius: 8px; margin-top: 24px; }}
.hint code {{ background: #fff; padding: 2px 6px; border-radius: 4px; font-family: ui-monospace, ""SFMono-Regular"", Menlo, monospace; }}
</style>
</head>
<body>
<span class=""badge"">{badge}</span>
<h1>{heading}</h1>
{displayLine}
{body}
<div class=""hint"">
<strong>下一步</strong><br>
回到 Lark 后,发送 <code>/model</code> 选择想用的模型,或 <code>/whoami</code> 查看当前绑定状态。
</div>
</body>
</html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult RenderBindingAcceptedHtmlInternal(
        string? displayName,
        ChannelIdentityOAuthAcceptedReceipt receipt)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var displayLine = string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : $"<p>账号:{System.Net.WebUtility.HtmlEncode(displayName)}</p>";
        var commandId = System.Net.WebUtility.HtmlEncode(receipt.CommandId);
        var html = $@"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>NyxID 绑定 — 已受理</title>
<style>
body {{ font-family: -apple-system, ""Segoe UI"", ""PingFang SC"", ""Microsoft YaHei"", sans-serif; max-width: 480px; margin: 60px auto; padding: 0 20px; color: #1d1d1f; line-height: 1.6; }}
.badge {{ display: inline-block; padding: 4px 10px; background: #fff4cc; color: #7a4d00; border-radius: 999px; font-size: 13px; font-weight: 500; }}
h1 {{ font-size: 22px; margin: 16px 0 8px; }}
.hint {{ background: #f5f5f7; padding: 16px 20px; border-radius: 8px; margin-top: 24px; }}
.hint code {{ background: #fff; padding: 2px 6px; border-radius: 4px; font-family: ui-monospace, ""SFMono-Regular"", Menlo, monospace; }}
</style>
</head>
<body>
<span class=""badge"">已受理</span>
<h1>NyxID 绑定请求已受理</h1>
{displayLine}
<p>可以关闭此页,回到 Lark 稍后继续对话。请求编号:<code>{commandId}</code></p>
<div class=""hint"">
<strong>下一步</strong><br>
回到 Lark 后,发送 <code>/whoami</code> 查看绑定状态。状态可见后,发送 <code>/model</code> 选择想用的模型。
</div>
</body>
</html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
