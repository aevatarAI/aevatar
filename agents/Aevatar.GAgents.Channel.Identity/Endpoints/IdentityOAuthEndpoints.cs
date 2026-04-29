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
/// HTTP endpoints owned by the Channel.Identity module:
/// <list type="bullet">
///   <item>
///     <c>GET /api/oauth/nyxid-callback</c> — OAuth Authorization Code redirect
///     target. Decodes the HMAC-sealed <c>state</c>, exchanges <c>code</c> for
///     <c>binding_id</c> via <see cref="INyxIdBrokerCallbackClient"/>, commits
///     the binding to <see cref="ExternalIdentityBindingGAgent"/>, and waits
///     for the projection to catch up via
///     <see cref="IProjectionReadinessPort"/> before responding.
///   </item>
///   <item>
///     <c>POST /api/webhooks/nyxid-broker-revocation</c> — receives NyxID's
///     Continuous Access Evaluation webhook on user-side revoke and
///     event-sources the local binding actor revoke (see ADR-0017 §Decision).
///   </item>
/// </list>
/// </summary>
public static class IdentityOAuthEndpoints
{
    private static readonly TimeSpan ProjectionWaitTimeout = TimeSpan.FromSeconds(3);

    // Webhook payloads from NyxID's CAE channel are small structured JSON
    // notifications; cap the read so a malicious or misconfigured caller
    // cannot exhaust memory by streaming an unbounded body. (deepseek-v4-pro
    // L219 security)
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
            return Results.BadRequest(new
            {
                error,
                detail = "NyxID returned an error on the OAuth callback. Re-run /init from Lark to retry.",
            });
        }

        if (string.IsNullOrWhiteSpace(code))
            return Results.BadRequest(new { error = "code_missing" });
        if (string.IsNullOrWhiteSpace(state))
            return Results.BadRequest(new { error = "state_missing" });

        if (!brokerCallback.TryDecodeStateToken(state, out var correlationId, out var subject, out var verifier, out var stateError) ||
            subject is null)
        {
            logger.LogWarning("OAuth callback rejected state token: {ErrorCode}", stateError);
            return Results.BadRequest(new
            {
                error = stateError,
                detail = "绑定链接已过期或无效,请回到 Lark 重新发送 /init",
            });
        }

        BrokerAuthorizationCodeResult exchange;
        try
        {
            exchange = await brokerCallback.ExchangeAuthorizationCodeAsync(code, verifier, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback authorization-code exchange failed for correlation {CorrelationId}", correlationId);
            return Results.Json(new
            {
                error = "token_exchange_failed",
                detail = "NyxID 绑定失败,稍后重试 /init",
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        var actorId = subject.ToActorId();

        // Concurrent /init protection at the callback layer: if the subject
        // is already bound, the freshly-issued binding_id we just got from
        // NyxID is an orphan. Best-effort revoke at NyxID before responding
        // so the orphan does not accumulate. (codex L68 MAJOR — actor-side
        // also discards the duplicate; this layer cleans up the remote.)
        if (await queryPort.ResolveAsync(subject, ct).ConfigureAwait(false) is not null)
        {
            await TryRevokeOrphanBindingAsync(brokerCallback, exchange.BindingId, logger, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                status = "already_bound",
                detail = "已绑定 NyxID 账号,可以回到 Lark 继续对话",
            });
        }

        var actor = await TryActivateActorAsync(actorRuntime, actorId, logger, ct).ConfigureAwait(false);
        if (actor is null)
        {
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

        try
        {
            await projectionReadiness
                .WaitForBindingStateAsync(subject, exchange.BindingId, ProjectionWaitTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Binding has been written to the actor's event store; the
            // readmodel is still propagating. Tell the user to wait a moment
            // and resend rather than block longer (ADR-0017 §Implementation
            // Notes #3).
            logger.LogWarning(
                "Projection readiness timed out for actor={ActorId}, expected binding={BindingId}; binding is committed but readmodel is lagging",
                actorId,
                exchange.BindingId);
            return Results.Json(new
            {
                status = "binding_pending_propagation",
                detail = "绑定已写入,稍后重发消息即可生效",
            });
        }

        // Mask the sub claim for display: never echo the full opaque sub.
        var displayName = ResolveDisplayName(exchange.IdToken);

        logger.LogInformation(
            "Bound external identity {Platform}:{Tenant}:{User} -> binding_id={BindingId}",
            subject.Platform,
            subject.Tenant,
            subject.ExternalUserId,
            exchange.BindingId);

        return Results.Ok(new
        {
            status = "bound",
            detail = displayName is null
                ? "已绑定 NyxID 账号,可以回到 Lark 继续对话"
                : $"已绑定 NyxID 账号({displayName}),可以回到 Lark 继续对话",
        });
    }

    private static string? ResolveDisplayName(string? idToken)
    {
        // Lightweight peek: id_token is a JWT, claims live in the second
        // base64url segment. Don't cryptographically verify here — the OAuth
        // exchange itself authenticated the response. We only use the value
        // to render a friendly bind-confirmation message and never persist it.
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
        INyxIdBrokerCallbackClient broker,
        string bindingId,
        ILogger logger,
        CancellationToken ct)
    {
        if (broker is not Aevatar.GAgents.Channel.Identity.Broker.NyxIdRemoteCapabilityBroker remote)
        {
            logger.LogDebug("Skipping orphan binding revoke: broker callback client is not the production broker");
            return;
        }

        try
        {
            await remote.RevokeBindingByIdAsync(bindingId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup; do not block the user response. NyxID's
            // own reaper is expected to clear stale bindings (per ADR-0017
            // §Implementation Notes #2).
            logger.LogWarning(ex, "Best-effort revoke of orphan binding {BindingId} failed", bindingId);
        }
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
            // Cap the read at MaxWebhookBodyBytes so a misconfigured caller
            // cannot exhaust memory (deepseek-v4-pro L219 security). Reject
            // anything larger as malformed — CAE webhook payloads are small.
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

        var validation = webhookValidator.Validate(http, bodyBytes);
        if (!validation.Succeeded)
        {
            logger.LogWarning(
                "Broker revocation webhook rejected: code={ErrorCode}",
                validation.ErrorCode);
            return Results.Unauthorized();
        }

        var notification = validation.Notification!;
        if (notification.ExternalSubject is null)
        {
            logger.LogWarning("Broker revocation webhook missing external_subject; dropping");
            return Results.BadRequest(new { error = "external_subject_missing" });
        }

        var actorId = notification.ExternalSubject.ToActorId();
        try
        {
            var actor = await actorRuntime.CreateAsync<ExternalIdentityBindingGAgent>(actorId, ct).ConfigureAwait(false);
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
            // Don't leak actor / runtime exception details into the webhook
            // response that NyxID will surface in its logs (mimo-v2.5-pro L194).
            // Log internally and return a generic 500 so NyxID can retry per
            // its CAE retry policy.
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
}
