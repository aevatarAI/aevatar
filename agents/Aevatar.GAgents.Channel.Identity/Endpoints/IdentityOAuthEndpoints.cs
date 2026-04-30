using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Endpoints;

/// <summary>
/// HTTP endpoints owned by the Channel.Identity module. See
/// <c>MapIdentityOAuthEndpoints</c> for the route table.
/// </summary>
public static class IdentityOAuthEndpoints
{
    private static readonly TimeSpan ProjectionWaitTimeout = TimeSpan.FromSeconds(3);
    private const int MaxWebhookBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapIdentityOAuthEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }

    // ─── OAuth callback ───

    internal static async Task<IResult> HandleNyxIdOAuthCallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromServices] INyxIdBrokerCallbackClient brokerCallback,
        [FromServices] IExternalIdentityBindingQueryPort queryPort,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IProjectionReadinessPort projectionReadiness,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
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
                detail = "Aevatar 已注册到 NyxID,但 OAuth client 未授予 broker capability — DCR 自举正常路径下 scope 会包含 urn:nyxid:scope:broker_binding。请检查 /api/oauth/aevatar-client/status 是否显示 client_id 与 allowed_scopes 一致;若是手动创建的 client,请通过 NyxID admin 把 broker_binding scope 加入 allowed_scopes 后再重试 /init。",
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
            return Results.Ok(new { status = "already_bound", detail = "已绑定 NyxID 账号,可以回到 Lark 继续对话" });
        }

        var actor = await TryActivateActorAsync(actorRuntime, actorId, logger, ct).ConfigureAwait(false);
        if (actor is null)
        {
            // Actor activation failed — the binding_id we just got from NyxID
            // would otherwise leak (no local actor will ever commit it). Best-
            // effort revoke at NyxID before responding so the orphan does not
            // accumulate. Same cleanup pattern as the already-bound branch
            // above (PR #521 codex/glm review).
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Json(new
            {
                error = "actor_activation_failed",
                detail = "NyxID 绑定失败,稍后重试 /init",
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var commitEnvelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new CommitBindingCommand
            {
                ExternalSubject = subject.Clone(),
                BindingId = exchange.BindingId,
            }),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actorId },
            },
        };
        await actor.HandleEventAsync(commitEnvelope, ct).ConfigureAwait(false);

        // Observe broker capability on the cluster client (idempotent) — first
        // successful binding_id is proof that NyxID admin enabled the flag.
        try
        {
            var clientActor = await actorRuntime
                .CreateAsync<AevatarOAuthClientGAgent>(AevatarOAuthClientGAgent.WellKnownId, ct)
                .ConfigureAwait(false);
            await clientActor.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new ObserveBrokerCapabilityCommand()),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = AevatarOAuthClientGAgent.WellKnownId },
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record broker capability observation; continuing");
        }

        try
        {
            await projectionReadiness
                .WaitForBindingStateAsync(subject, exchange.BindingId, ProjectionWaitTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Projection readiness timed out for actor={ActorId}, expected binding={BindingId}",
                actorId,
                exchange.BindingId);
            return Results.Json(new
            {
                status = "binding_pending_propagation",
                detail = "绑定已写入,稍后重发消息即可生效",
            });
        }

        var displayName = ResolveDisplayName(exchange.IdToken);
        logger.LogInformation(
            "Bound external identity {Platform}:{Tenant}:{User} -> binding_id={BindingId}",
            subject.Platform, subject.Tenant, subject.ExternalUserId, exchange.BindingId);

        return Results.Ok(new
        {
            status = "bound",
            detail = displayName is null
                ? "已绑定 NyxID 账号,可以回到 Lark 继续对话"
                : $"已绑定 NyxID 账号({displayName}),可以回到 Lark 继续对话",
        });
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
            var redirectUriDrifted = string.IsNullOrEmpty(snapshot.RedirectUri)
                || !string.Equals(snapshot.RedirectUri, resolvedRedirectUri, StringComparison.Ordinal);
            var status = redirectUriDrifted
                ? "redirect_uri_drifted"
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
                broker_capability_observed = snapshot.BrokerCapabilityObserved,
                broker_capability_observed_at = snapshot.BrokerCapabilityObservedAt,
                ops_handoff = snapshot.BrokerCapabilityObserved
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

    // ─── Broker revocation webhook ───

    internal static async Task<IResult> HandleBrokerRevocationWebhookAsync(
        HttpContext http,
        [FromServices] BrokerRevocationWebhookValidator webhookValidator,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
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
            var actor = await actorRuntime
                .CreateAsync<ExternalIdentityBindingGAgent>(actorId, ct)
                .ConfigureAwait(false);
            var revokeEnvelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new RevokeBindingCommand
                {
                    ExternalSubject = notification.ExternalSubject.Clone(),
                    Reason = string.IsNullOrWhiteSpace(notification.Reason)
                        ? "nyxid_cae_revocation"
                        : notification.Reason,
                }),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actorId },
                },
            };
            await actor.HandleEventAsync(revokeEnvelope, ct).ConfigureAwait(false);
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

    private static async Task<Aevatar.Foundation.Abstractions.IActor?> TryActivateActorAsync(
        IActorRuntime runtime,
        string actorId,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            return await runtime.CreateAsync<ExternalIdentityBindingGAgent>(actorId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to activate ExternalIdentityBindingGAgent for actor={ActorId}", actorId);
            return null;
        }
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
        catch (Exception)
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
}
