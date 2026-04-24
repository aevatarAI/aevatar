using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private const string RelayCardActionContentType = "card_action";
    private static readonly JsonSerializerOptions RelayJsonOptions = NyxIdRelayPayloads.JsonOptions;

    /// <summary>
    /// Receives forwarded platform messages from NyxID Channel Bot Relay.
    /// Validates the Nyx relay token, dispatches the inbound turn into ConversationGAgent,
    /// and returns 202 immediately. Workflow card actions still short-circuit through the
    /// dedicated relay card handler because they are not message turns.
    /// </summary>
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
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

            NyxIdRelayCallbackPayload? payload;
            RelayMessage? relayMessage;
            try
            {
                payload = JsonSerializer.Deserialize<NyxIdRelayCallbackPayload>(bodyBytes, RelayJsonOptions);
                relayMessage = JsonSerializer.Deserialize<RelayMessage>(bodyBytes, RelayJsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse relay payload");
                return Results.BadRequest(new { error = "invalid_relay_payload" });
            }

            if (payload is null)
                return Results.BadRequest(new { error = "missing_relay_payload" });
            if (string.IsNullOrWhiteSpace(payload.MessageId))
                return Results.BadRequest(new { error = "missing_message_id" });

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

            var contentType = NyxIdRelayPayloads.NormalizeContentType(payload.Content?.ContentType ?? payload.Content?.Type);
            if (string.Equals(contentType, RelayCardActionContentType, StringComparison.Ordinal))
            {
                relayMessage ??= BuildRelayMessage(payload);
                if (await TryHandleRelayWorkflowCardActionAsync(http, relayMessage, logger, ct) is { } workflowResult)
                    return workflowResult;

                logger.LogInformation(
                    "Ignored unsupported relay card action: message={MessageId}, conversation={ConversationId}",
                    payload.MessageId,
                    payload.Conversation?.Id ?? payload.Conversation?.PlatformId);
                return Results.Accepted(value: new
                {
                    status = "ignored",
                    reason = "unsupported_card_action",
                    message_id = payload.MessageId,
                });
            }

            var parsed = relayTransport.Parse(bodyBytes);
            if (!parsed.Success)
            {
                if (parsed.Ignored)
                {
                    return Results.Accepted(value: new
                    {
                        status = "ignored",
                        reason = parsed.ErrorCode,
                        detail = parsed.ErrorSummary,
                    });
                }

                return Results.BadRequest(new
                {
                    error = parsed.ErrorCode,
                    detail = parsed.ErrorSummary,
                });
            }

            var activity = parsed.Activity!.Clone();
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
            var relayInbound = new NyxRelayInboundActivity
            {
                Activity = activity,
                ReplyToken = payload.ReplyToken?.Trim() ?? string.Empty,
                ReplyTokenExpiresAtUnixMs = ResolveReplyTokenExpiresAtUnixMs(payload.ReplyToken, relayOptions),
                CorrelationId = activity.OutboundDelivery.CorrelationId,
            };

            var actorId = ConversationGAgent.BuildActorId(activity.Conversation.CanonicalKey);
            var actor = await actorRuntime.CreateAsync<ConversationGAgent>(actorId, ct);
            var command = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(relayInbound),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actorId },
                },
            };

            await actor.HandleEventAsync(command, ct);

            logger.LogInformation(
                "Accepted relay callback into channel conversation backbone: message={MessageId}, actor={ActorId}, platform={Platform}",
                activity.Id,
                actorId,
                activity.ChannelId?.Value);

            return Results.Accepted(value: new
            {
                status = "accepted",
                message_id = activity.Id,
                actor_id = actorId,
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

    private static async Task<IResult?> TryHandleRelayWorkflowCardActionAsync(
        HttpContext http,
        RelayMessage message,
        ILogger logger,
        CancellationToken ct) =>
        await NyxIdRelayWorkflowCards.TryHandleAsync(http, message, logger, ct);

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

    private static RelayMessage BuildRelayMessage(NyxIdRelayCallbackPayload payload) =>
        new()
        {
            MessageId = payload.MessageId,
            Platform = payload.Platform,
            Agent = payload.Agent is null
                ? null
                : new RelayAgent
                {
                    ApiKeyId = payload.Agent.ApiKeyId,
                    Name = payload.Agent.Name,
                },
            Conversation = payload.Conversation is null
                ? null
                : new RelayConversation
                {
                    Id = payload.Conversation.Id,
                    PlatformId = payload.Conversation.PlatformId,
                    Type = payload.Conversation.Type ?? payload.Conversation.ConversationType,
                },
            Sender = payload.Sender is null
                ? null
                : new RelaySender
                {
                    PlatformId = payload.Sender.PlatformId,
                    DisplayName = payload.Sender.DisplayName,
                },
            Content = payload.Content is null
                ? null
                : new RelayContent
                {
                    ContentType = payload.Content.ContentType,
                    Type = payload.Content.Type,
                    Text = payload.Content.Text,
                },
            Timestamp = payload.Timestamp,
        };

    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);
}
