using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Any = Google.Protobuf.WellKnownTypes.Any;

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
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorDispatchPort dispatchPort,
        [FromServices] NyxIdRelayTransport relayTransport,
        [FromServices] NyxIdRelayAuthValidator relayAuthValidator,
        [FromServices] Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
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
            var scopeId = ResolveRelayScopeId(validation.ScopeId);
            if (string.IsNullOrWhiteSpace(scopeId))
            {
                logger.LogWarning(
                    "Relay callback authentication succeeded but did not resolve a canonical scope id: message={MessageId}, apiKeyId={ApiKeyId}",
                    payload.MessageId,
                    payload.Agent?.ApiKeyId);
                return Results.Unauthorized();
            }

            var relayIdentity = NormalizeOptional(validation.RelayApiKeyId);
            if (relayIdentity is null)
            {
                logger.LogWarning(
                    "Relay callback authentication succeeded but did not resolve a relay identity: message={MessageId}, apiKeyId={ApiKeyId}",
                    payload.MessageId,
                    payload.Agent?.ApiKeyId);
                return Results.Unauthorized();
            }

            if (parsed.Ignored)
            {
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
            activity.TransportExtras.ValidatedScopeId = scopeId;
            var relayInbound = new NyxRelayInboundActivity
            {
                Activity = activity,
                ReplyToken = payload.ReplyToken?.Trim() ?? string.Empty,
                ReplyTokenExpiresAtUnixMs = ResolveReplyTokenExpiresAtUnixMs(payload.ReplyToken, relayOptions),
                CorrelationId = activity.OutboundDelivery.CorrelationId,
            };

            var actorId = BuildRelayConversationActorId(relayIdentity, activity.Conversation.CanonicalKey);
            var actor = await actorRuntime.GetAsync(actorId)
                ?? await actorRuntime.CreateAsync<ConversationGAgent>(actorId, ct);
            var command = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(relayInbound),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            await dispatchPort.DispatchAsync(actor.Id, command, ct);

            logger.LogInformation(
                "Accepted relay callback into channel conversation backbone: message={MessageId}, actor={ActorId}, platform={Platform}, activity={ActivityType}",
                activity.Id,
                actor.Id,
                activity.ChannelId?.Value,
                activity.Type);

            return Results.Accepted(value: new
            {
                status = "accepted",
                message_id = activity.Id,
                actor_id = actor.Id,
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

    private static string? ResolveRelayScopeId(string? validatedScopeId)
    {
        var scopeId = NormalizeOptional(validatedScopeId);
        if (scopeId is not null)
            return scopeId;

        return null;
    }

    private static string BuildRelayConversationActorId(string relayIdentity, string canonicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relayIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalKey);

        var actorKey = $"{relayIdentity.Trim()}\n{canonicalKey.Trim()}";
        var relayHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actorKey)))
            .ToLowerInvariant();
        return $"channel-conversation:relay:{relayHash}";
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);
}
