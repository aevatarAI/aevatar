using System.IdentityModel.Tokens.Jwt;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    /// <summary>
    /// Receives forwarded platform messages from NyxID Channel Bot Relay.
    /// Validates the relay callback, asks the relay transport to normalize the payload into
    /// a <see cref="ChatActivity"/> (text messages and card actions alike), then publishes it
    /// into the scoped <see cref="ConversationGAgent"/> inbox. All downstream business routing
    /// (slash commands, agent-builder cards, workflow resume cards) is the responsibility of
    /// <c>ChannelConversationTurnRunner</c> so the webhook stays a thin adapter.
    /// </summary>
    // Refactor (iter17/cluster-038):
    //   Old pattern: Nyx relay replay/idempotency 和 reply 累积在 process-local ConcurrentDictionary/lock(NyxRelayBridgeIdempotencyGuard / NyxIdRelayReplayGuard / NyxIdRelayReplyAccumulator)。
    //   New principle: ConversationGAgent persist callback_jti admission 为 typed event 优先于 business work;删除 process-local replay guards + dead accumulator。
    // Refactor (iter113/cluster-113-telegram-connector-inmemory-updates):
    //   Old pattern: Telegram connector keeps inbound updates as in-memory state (process-local queue/dictionary).
    //   New principle: Delete telegram_user /getUpdates in-memory queue and route inbound Telegram through existing NyxID relay/proxy; no new actor type; no in-memory state on connector side.
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] INyxIdRelayIngressPort relayIngressPort,
        [FromServices] NyxIdRelayTransport relayTransport,
        [FromServices] NyxIdRelayAuthValidator relayAuthValidator,
        [FromServices] Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
        [FromServices] Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver nyxIdCurrentUserResolver,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Relay");

        try
        {
            byte[] bodyBytes;
            await using (var body = new MemoryStream())
            {
                await http.Request.Body.CopyToAsync(body, ct);
                bodyBytes = body.ToArray();
            }

            var parsed = relayTransport.Parse(bodyBytes);
            if (parsed.Payload is null)
            {
                return Results.BadRequest(new
                {
                    error = string.IsNullOrWhiteSpace(parsed.ErrorCode) ? "invalid_relay_payload" : parsed.ErrorCode,
                    detail = parsed.ErrorSummary,
                });
            }

            var payload = parsed.Payload;
            var validation = await relayAuthValidator.ValidateAsync(http, bodyBytes, payload, ct);
            if (!validation.Succeeded || validation.Principal is null)
            {
                logger.LogWarning(
                    "Relay callback authentication failed: code={Code}, detail={Detail}",
                    validation.ErrorCode,
                    validation.ErrorSummary);
                return Results.Unauthorized();
            }

            http.User = validation.Principal;
            var scopeId = await ResolveRelayScopeIdAsync(
                validation.ScopeId,
                validation.UserAccessToken,
                payload,
                http.RequestServices,
                logger,
                ct);
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                logger.LogWarning(
                    "Relay callback authentication succeeded but did not resolve a canonical scope id: message={MessageId}, apiKeyId={ApiKeyId}",
                    payload.MessageId,
                    payload.Agent?.ApiKeyId);
                return Results.Unauthorized();
            }

            if (parsed.Ignored)
            {
                // This branch produces no user-visible reply; without a log line a dropped
                // turn (e.g. an unrecognized content type) is completely silent and
                // undiagnosable. Record the reason so future drops surface in logs.
                logger.LogInformation(
                    "Relay payload ignored: reason={Reason}, detail={Detail}, message={MessageId}, platform={Platform}, contentType={ContentType}",
                    parsed.ErrorCode,
                    parsed.ErrorSummary,
                    payload.MessageId,
                    payload.Platform,
                    payload.Content?.ContentType ?? payload.Content?.Type);
                return Results.Accepted(value: new
                {
                    status = "ignored",
                    reason = parsed.ErrorCode,
                    detail = parsed.ErrorSummary,
                });
            }

            if (!parsed.Success || parsed.Activity is null)
            {
                return Results.BadRequest(new
                {
                    error = string.IsNullOrWhiteSpace(parsed.ErrorCode) ? "invalid_relay_payload" : parsed.ErrorCode,
                    detail = parsed.ErrorSummary,
                });
            }

            var activity = parsed.Activity.Clone();
            if (string.IsNullOrWhiteSpace(activity.Conversation?.CanonicalKey))
            {
                return Results.BadRequest(new
                {
                    error = "conversation_key_missing",
                    detail = "Relay payload did not resolve to a canonical conversation key.",
                });
            }

            activity.OutboundDelivery ??= new OutboundDeliveryContext();
            activity.TransportExtras ??= new TransportExtras();
            activity.TransportExtras.NyxUserAccessToken = validation.UserAccessToken ?? string.Empty;
            activity.TransportExtras.NyxRegistrationScopeId = scopeId.Trim();
            // Resolve sender NyxID at ingress so the actor can build a per-user
            // caller scope for chat-route policy lookup without making an HTTP
            // call inside the turn. Fail-soft: log + leave empty so policy
            // resolution falls through to scope-only / default policies.
            activity.TransportExtras.NyxSenderUserId =
                await TryResolveSenderNyxUserIdAsync(
                    nyxIdCurrentUserResolver,
                    validation.UserAccessToken,
                    logger,
                    ct);
            // Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
            // Relay endpoint validates NyxID callback/HMAC/user token and maps the typed activity only.
            // Conversation actor creation and dispatch are owned by the relay ingress port.
            // This keeps Host runtime-neutral without requiring any NyxID repository change.
            var accepted = await relayIngressPort.AcceptAsync(
                new NyxIdRelayIngressRequest(
                    scopeId,
                    activity,
                    payload.ReplyToken,
                    ResolveReplyTokenExpiresAtUnixMs(payload.ReplyToken, relayOptions),
                    validation.RelayApiKeyId,
                    validation.CallbackJti,
                    validation.CallbackObservedAtUnixMs,
                    validation.CallbackReplayExpiresAtUnixMs),
                ct);

            // Best-effort activation marker for cross-account /channels status. Fire-and-forget:
            // it runs AFTER the inbound is accepted and is NOT awaited, so it can never add
            // latency to, stall, or fail the relay response (the sensitive path that previously
            // went silent) — including turning a request cancellation into a 499 post-acceptance.
            // The recorder is a singleton (safe to use after the request scope disposes) and
            // swallows its own errors; CancellationToken.None so request completion doesn't
            // cancel the in-flight signal.
            var activityRecorder = http.RequestServices.GetService<IChannelRelayActivityRecorder>();
            if (activityRecorder is not null)
            {
                var relayApiKeyId = validation.RelayApiKeyId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await activityRecorder.RecordInboundAsync(relayApiKeyId, CancellationToken.None);
                    }
                    catch (Exception recordEx)
                    {
                        logger.LogWarning(recordEx, "Relay activity recording failed (non-fatal): apiKeyId={ApiKeyId}", relayApiKeyId);
                    }
                });
            }

            return Results.Accepted(value: new
            {
                status = "accepted",
                message_id = accepted.MessageId,
                actor_id = accepted.ActorId,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Relay handler unexpected error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static long ResolveReplyTokenExpiresAtUnixMs(
        string? replyToken,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions)
    {
        var fallback = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, relayOptions.RelayReplyTokenRuntimeTtlSeconds));
        if (string.IsNullOrWhiteSpace(replyToken))
            return fallback.ToUnixTimeMilliseconds();

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(replyToken.Trim());
            return jwt.ValidTo == DateTime.MinValue
                ? fallback.ToUnixTimeMilliseconds()
                : new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }
        catch (ArgumentException)
        {
            return fallback.ToUnixTimeMilliseconds();
        }
    }

    private static async Task<string?> ResolveRelayScopeIdAsync(
        string? validatedScopeId,
        string? userAccessToken,
        NyxIdRelayCallbackPayload payload,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct)
    {
        var scopeId = NormalizeOptional(validatedScopeId);
        if (scopeId is not null)
            return scopeId;

        var nyxAgentApiKeyId = NormalizeOptional(payload.Agent?.ApiKeyId);

        // 1) Authoritative: the api-key -> scope mirror, populated when the bot was registered through
        //    aevatar. The relay callback token carries no scope claim, so for mirror-registered bots this
        //    is the only scope source.
        if (nyxAgentApiKeyId is not null)
        {
            var scopeResolver = services.GetService<INyxIdRelayScopeResolver>();
            if (scopeResolver is not null)
            {
                try
                {
                    var resolvedScopeId = NormalizeOptional(await scopeResolver.ResolveScopeIdByApiKeyAsync(nyxAgentApiKeyId, ct));
                    if (resolvedScopeId is not null)
                    {
                        logger.LogInformation(
                            "Resolved relay callback scope id from relay scope resolver: message={MessageId}, apiKeyId={ApiKeyId}",
                            payload.MessageId,
                            nyxAgentApiKeyId);
                        return resolvedScopeId;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to resolve relay callback scope id from channel bot registration: message={MessageId}, apiKeyId={ApiKeyId}",
                        payload.MessageId,
                        nyxAgentApiKeyId);
                }
            }
        }

        // 2) Fallback: a bot registered directly on NyxID has no aevatar mirror entry and therefore no
        //    api-key -> scope mapping. Derive the scope from the bot owner's identity carried by the relay
        //    user token (scope_id ?? uid ?? sub), matching the aevatar claims waterfall. This is correct
        //    when the bot's scope is the owner's NyxID identity (the default); a bot deliberately bound to
        //    a distinct aevatar scope still requires its mirror entry.
        var ownerScopeId = ResolveScopeIdFromUserToken(userAccessToken);
        if (ownerScopeId is not null)
        {
            logger.LogInformation(
                "Resolved relay callback scope id from bot-owner identity (no mirror entry): message={MessageId}, apiKeyId={ApiKeyId}",
                payload.MessageId,
                nyxAgentApiKeyId);
        }

        return ownerScopeId;
    }

    /// <summary>
    /// Reads the aevatar tenant scope from the relay user token's identity claims
    /// (<c>scope_id</c> ?? <c>uid</c> ?? <c>sub</c>), matching the registration-time claims waterfall.
    /// The token's authenticity is already established by the validated callback token, so the claims are
    /// read without re-validating the signature.
    /// </summary>
    internal static string? ResolveScopeIdFromUserToken(string? userAccessToken)
    {
        var token = NormalizeOptional(userAccessToken);
        if (token is null)
            return null;

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return NormalizeOptional(jwt.Claims.FirstOrDefault(claim => claim.Type == "scope_id")?.Value)
                ?? NormalizeOptional(jwt.Claims.FirstOrDefault(claim => claim.Type == "uid")?.Value)
                ?? NormalizeOptional(jwt.Subject);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string> TryResolveSenderNyxUserIdAsync(
        Aevatar.GAgents.Scheduled.INyxIdCurrentUserResolver resolver,
        string? userAccessToken,
        ILogger logger,
        CancellationToken ct)
    {
        var token = NormalizeOptional(userAccessToken);
        if (token is null)
            return string.Empty;

        try
        {
            var resolved = NormalizeOptional(await resolver.ResolveCurrentUserIdAsync(token, ct));
            return resolved ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve sender NyxID at relay ingress; chat-routing per-user policies will not match for this turn.");
            return string.Empty;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);
}
