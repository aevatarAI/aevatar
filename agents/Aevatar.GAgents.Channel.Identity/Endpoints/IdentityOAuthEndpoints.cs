using System.Security.Cryptography;
using System.Text;
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
            .AllowAnonymous();
        app.MapPost("/api/webhooks/nyxid-broker-revocation", HandleBrokerRevocationWebhookAsync)
            .WithTags("ChannelIdentity")
            .AllowAnonymous();
        app.MapGet("/api/oauth/aevatar-client/status", HandleAevatarOAuthClientStatusAsync)
            .WithTags("ChannelIdentity")
            .AllowAnonymous();
        // Operator-only: rebuild the cluster-singleton OAuth client snapshot
        // to point at an admin-supplied client_id (issue #549 production
        // unblock). Auth is by static admin token header — see
        // AevatarOAuthAdminOptions. AllowAnonymous because the auth check is
        // done inline; no ASP.NET auth handler is wired for this module. The
        // RebuildAuthEndpointFilter rejects unauthenticated callers BEFORE
        // model binding / DI resolution so a flooded admin-token-less request
        // does not run through deserialization and DI on every call.
        app.MapPost("/api/oauth/aevatar-client/rebuild", HandleAevatarOAuthClientRebuildAsync)
            .WithTags("ChannelIdentity")
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
        [FromServices] IExternalIdentityBindingQueryPort queryPort,
        [FromServices] ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
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
            // 400 path instead of letting the generic catch return 502
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
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback authorization-code exchange failed for correlation {CorrelationId}", decode.CorrelationId);
            return Results.Json(new
            {
                error = "token_exchange_failed",
                detail = "NyxID 绑定失败,稍后重试 /init",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        // Defensive: NyxID returned no binding_id even though authorization-code
        // exchange succeeded. Post NyxID#576 fix, broker mode is triggered by
        // EITHER `broker_capability_enabled=true` OR `urn:nyxid:scope:broker_binding`
        // appearing in the client's `allowed_scopes` (oauth_broker_service.rs
        // is_broker_client). Aevatar's DCR call always requests that scope, so
        // the happy path returns a binding_id automatically — no ops handoff.
        // Reaching this branch implies the client was misregistered (e.g. an
        // operator-provisioned confidential client without the scope, or a DCR
        // race that pre-dates the NyxID#576 fix). Log + 409 with diagnostic.
        if (string.IsNullOrEmpty(exchange.BindingId))
        {
            logger.LogWarning(
                "OAuth callback succeeded but NyxID did not return a binding_id — the OAuth client is registered without broker capability. Expected `urn:nyxid:scope:broker_binding` in allowed_scopes (DCR self-bootstrap requests this) or `broker_capability_enabled=true` on a manually provisioned client.");
            return Results.Json(new
            {
                status = "broker_capability_disabled",
                detail = "Aevatar 已注册到 NyxID,但 OAuth client 未授予 broker capability — DCR 自举正常路径下 scope 会包含 urn:nyxid:scope:broker_binding 与 proxy。请检查 /api/oauth/aevatar-client/status 是否显示 client_id 与 allowed_scopes 一致;若是手动创建的 client,请通过 NyxID admin 把 broker_binding/proxy scope 加入 allowed_scopes 后再重试 /init。",
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var actorId = subject.ToActorId();

        if (await queryPort.ResolveAsync(subject, ct).ConfigureAwait(false) is not null)
        {
            // Concurrent /init protection: if the subject is already bound,
            // the freshly-issued binding_id we just got from NyxID is an
            // orphan. Best-effort revoke at NyxID before responding so the
            // orphan does not accumulate at NyxID with no local reference.
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return RenderBoundSuccess(displayName: null, alreadyBound: true, format: format);
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            accepted = await bindingDispatch
                .DispatchAsync(new CommitBindingCommand
                {
                    ExternalSubject = subject.Clone(),
                    BindingId = exchange.BindingId,
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback failed to dispatch CommitBindingCommand for actor={ActorId}", actorId);
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
                "OAuth callback dispatch rejected for actor={ActorId}: error={Error}",
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
            "Accepted external identity binding dispatch {Platform}:{Tenant}:{User} -> binding_id={BindingId}, command_id={CommandId}",
            subject.Platform,
            subject.Tenant,
            subject.ExternalUserId,
            exchange.BindingId,
            accepted.Receipt.CommandId);

        return RenderBindingAccepted(displayName, accepted.Receipt, format);
    }

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
            var oauthScopeDrifted = !AevatarOAuthClientScopes.ContainsRequiredScopes(snapshot.OauthScope);
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
                    ? "Bootstrap must re-run DCR so the OAuth client includes the proxy scope required by LLM route selection."
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
                detail = "Bootstrap service has not yet completed NyxID dynamic client registration. Wait or check the host startup logs.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // ─── Operator rebuild ───

    /// <summary>
    /// Body for <c>POST /api/oauth/aevatar-client/rebuild</c>. The operator
    /// supplies a fresh <c>client_id</c> (typically created via NyxID admin
    /// after a wedge — see issue #549) and the actor pins its snapshot to
    /// it. <c>redirect_uri</c> and <c>oauth_scope</c> are NOT operator-
    /// supplied fields: the endpoint always uses
    /// <see cref="NyxIdRedirectUriResolver"/> and
    /// <see cref="AevatarOAuthClientScopes.AuthorizationScope"/> respectively,
    /// otherwise the next bootstrap pass would observe drift and re-DCR
    /// away the freshly-pinned client (PR #570 review consensus on the
    /// drift bug + URL-validation surface).
    /// </summary>
    public sealed record RebuildAevatarOAuthClientRequest(
        string? client_id,
        long? client_id_issued_at_unix);

    internal static Task<IResult> HandleAevatarOAuthClientRebuildAsync(
        HttpContext http,
        [FromBody] RebuildAevatarOAuthClientRequest? body,
        [FromServices] IOptionsMonitor<AevatarOAuthAdminOptions> adminOptions,
        [FromServices] ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct) =>
        HandleAevatarOAuthClientRebuildCoreAsync(
            http,
            body,
            adminOptions,
            rebuildDispatch,
            loggerFactory,
            ct);

    /// <summary>
    /// Core method exposed for tests to pass admin options and the typed
    /// dispatch service directly, without resolving endpoint-bound services.
    /// </summary>
    internal static async Task<IResult> HandleAevatarOAuthClientRebuildCoreAsync(
        HttpContext http,
        RebuildAevatarOAuthClientRequest? body,
        IOptionsMonitor<AevatarOAuthAdminOptions> adminOptions,
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> rebuildDispatch,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Refactor (iter27/cluster-028-identity-oauth-endpoint):
        //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
        //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
        var logger = loggerFactory.CreateLogger("Aevatar.Channel.Identity.OAuthRebuild");

        var configuredToken = adminOptions.CurrentValue.RebuildToken;
        if (string.IsNullOrEmpty(configuredToken))
        {
            logger.LogWarning(
                "Rebuild endpoint invoked but ChannelIdentity:Admin:RebuildToken is unset; refusing fail-secure.");
            return Results.Json(new
            {
                error = "rebuild_not_configured",
                detail = "ChannelIdentity:Admin:RebuildToken is unset. Configure it (env var ChannelIdentity__Admin__RebuildToken) and redeploy before retrying.",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!http.Request.Headers.TryGetValue(AevatarOAuthAdminOptions.RebuildTokenHeader, out var presented)
            || !ConstantTimeEquals(configuredToken, presented.ToString()))
        {
            logger.LogWarning(
                "Rebuild endpoint rejected: missing or invalid {Header}.",
                AevatarOAuthAdminOptions.RebuildTokenHeader);
            return Results.Unauthorized();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.client_id))
        {
            return Results.BadRequest(new
            {
                error = "client_id_required",
                detail = "Body must include client_id (the NyxID-issued OAuth client_id this cluster should pin to).",
            });
        }

        var authority = NyxIdAuthorityResolver.Resolve(logger);
        var redirectUri = NyxIdRedirectUriResolver.Resolve(logger);
        var redirectUris = NyxIdRedirectUriResolver.ResolveRegisteredRedirectUris(logger);
        var oauthScope = AevatarOAuthClientScopes.AuthorizationScope;

        // Validate Unix-seconds before dispatching: AevatarOAuthClient
        // ProjectionProvider later calls DateTimeOffset.FromUnixTimeSeconds
        // on the persisted value, which throws ArgumentOutOfRangeException
        // for values like long.MaxValue. Surface the bad input as a 400
        // here instead of letting the read path crash on the next status
        // poll (codex P1 on PR #570).
        long issuedAtUnix;
        if (body.client_id_issued_at_unix is { } supplied)
        {
            try
            {
                _ = DateTimeOffset.FromUnixTimeSeconds(supplied);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new
                {
                    error = "client_id_issued_at_unix_invalid",
                    detail = "client_id_issued_at_unix must be a Unix-seconds value within DateTimeOffset range.",
                });
            }
            issuedAtUnix = supplied;
        }
        else
        {
            issuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> accepted;
        try
        {
            var command = new ProvisionAevatarOAuthClientCommand
            {
                ClientId = body.client_id!.Trim(),
                ClientIdIssuedAtUnix = issuedAtUnix,
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

        logger.LogWarning(
            "Operator rebuild accepted for AevatarOAuthClientGAgent: client_id={ClientId}, authority={Authority}, redirect_uri={RedirectUri}, command_id={CommandId}.",
            body.client_id,
            authority,
            redirectUri,
            accepted.Receipt.CommandId);

        return Results.Accepted(OAuthClientStatusUrl, new
        {
            status = "rebuild_pending",
            command_id = accepted.Receipt.CommandId,
            correlation_id = accepted.Receipt.CorrelationId,
            actor_id = accepted.Receipt.ActorId,
            status_url = OAuthClientStatusUrl,
            detail = "Provision command accepted for dispatch. Re-poll the status URL; it will reflect the new client_id once the actor commits and projection materializes.",
        });
    }

    /// <summary>
    /// Length-tolerant constant-time string compare. <c>FixedTimeEquals</c>
    /// itself returns false on length mismatch in O(1), which leaks the
    /// configured token's length to a timing observer — for an admin
    /// break-glass surface keyed on a high-entropy token this residual leak
    /// is acceptable (the attacker still has to brute-force the content).
    /// The earlier shape returned early on <c>right is null</c>; the call
    /// site short-circuits via <c>TryGetValue</c> so right is never null in
    /// practice, but we still treat null as empty to keep the helper's
    /// signature constant-time-uniform (PR #570 review, 4-model consensus).
    /// </summary>
    /// <remarks>
    /// SCOPE: this helper is intentionally <c>private static</c> and tied to
    /// the rebuild admin-token check. It is NOT for general callers — if a new
    /// caller needs constant-time string compare for a lower-entropy secret,
    /// the length leak above becomes material; do not promote this to
    /// internal/public without first replacing it with a length-padding scheme.
    /// </remarks>
    private static bool ConstantTimeEquals(string left, string? right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    /// <summary>
    /// Endpoint filter that performs the rebuild admin-token check before model binding
    /// and per-request DI activation kick in. Without this filter the handler method
    /// still rejects unauthenticated callers (it re-runs the same check inline), but
    /// every unauthenticated POST would needlessly deserialize the body and resolve
    /// command dispatch services — a small but real DoS amplifier on a /rebuild
    /// that is supposed to be operator-only break-glass.
    /// </summary>
    internal sealed class RebuildAuthEndpointFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var adminOptions = http.RequestServices
                .GetRequiredService<IOptionsMonitor<AevatarOAuthAdminOptions>>()
                .CurrentValue;
            var configuredToken = adminOptions.RebuildToken;
            if (string.IsNullOrEmpty(configuredToken))
            {
                // Fall through to the handler so it can return the standard
                // "rebuild_not_configured" 503; we don't want this filter to short-circuit
                // and bypass that explicit operator-facing error.
                return await next(context).ConfigureAwait(false);
            }

            if (!http.Request.Headers.TryGetValue(AevatarOAuthAdminOptions.RebuildTokenHeader, out var presented)
                || !ConstantTimeEquals(configuredToken, presented.ToString()))
            {
                return Results.Unauthorized();
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
