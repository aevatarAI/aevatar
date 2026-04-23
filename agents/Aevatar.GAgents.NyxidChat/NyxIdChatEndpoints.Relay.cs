using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.NyxidChat.Relay;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private const string RelayCardActionContentType = "card_action";
    private static readonly JsonSerializerOptions RelayJsonOptions = NyxIdRelayPayloads.JsonOptions;

    /// <summary>
    /// Receives forwarded platform messages from NyxID Channel Bot Relay.
    /// Validates the Nyx relay JWT, durably dispatches the inbound turn, returns 202,
    /// and sends the platform reply asynchronously through Nyx channel-relay/reply.
    /// </summary>
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] NyxIdRelayOptions relayOptions,
        [FromServices] NyxRelayJwtValidator relayJwtValidator,
        [FromServices] NyxIdApiClient nyxClient,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Relay");

        try
        {
            RelayMessage? message;
            try
            {
                message = await http.Request.ReadFromJsonAsync<RelayMessage>(RelayJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse relay payload");
                return Results.BadRequest(new { error = "invalid_relay_payload" });
            }

            if (message is null)
                return Results.BadRequest(new { error = "missing_relay_payload" });
            if (string.IsNullOrWhiteSpace(message.MessageId))
                return Results.BadRequest(new { error = "missing_message_id" });

            var contentType = NyxIdRelayPayloads.GetContentType(message.Content);
            var contentText = NormalizeOptional(message.Content?.Text);
            if (contentText is null)
                return Results.Accepted(value: new { status = "ignored", reason = "empty_text" });

            var userToken = http.Request.Headers["X-NyxID-User-Token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(userToken))
            {
                logger.LogWarning("Relay callback missing X-NyxID-User-Token header");
                return Results.Unauthorized();
            }

            var validation = await relayJwtValidator.ValidateAsync(userToken, ct);
            if (!validation.Succeeded || validation.Principal is null || string.IsNullOrWhiteSpace(validation.Subject))
            {
                logger.LogWarning("Relay callback JWT validation failed: {Error}", validation.Error);
                return Results.Unauthorized();
            }

            if (!string.IsNullOrWhiteSpace(message.Agent?.ApiKeyId) &&
                !string.Equals(message.Agent.ApiKeyId, validation.RelayApiKeyId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Relay callback agent mismatch: payload_agent_api_key_id={PayloadApiKeyId}, token_agent_api_key_id={TokenApiKeyId}",
                    message.Agent.ApiKeyId,
                    validation.RelayApiKeyId);
                return Results.Unauthorized();
            }

            http.User = validation.Principal;
            var scopeId = validation.Subject!;

            // Relay callbacks reuse the same user-config projection path as Studio and web chat.
            // After JWT validation we attach the relay principal to HttpContext.User so
            // IAppScopeResolver can resolve the same scope from the validated subject.

            if (string.Equals(contentType, RelayCardActionContentType, StringComparison.Ordinal))
            {
                if (await TryHandleRelayWorkflowCardActionAsync(http, message, logger, ct) is { } workflowResult)
                    return workflowResult;

                logger.LogInformation(
                    "Ignored unsupported relay card action: message={MessageId}, conversation={ConversationId}",
                    message.MessageId,
                    message.Conversation?.Id ?? message.Conversation?.PlatformId);
                return Results.Accepted(value: new
                {
                    status = "ignored",
                    reason = "unsupported_card_action",
                    message_id = message.MessageId,
                });
            }

            var platform = message.Platform ?? "unknown";
            var conversationPlatformId = message.Conversation?.PlatformId ?? "unknown";
            var conversationId = message.Conversation?.Id;
            if (string.IsNullOrWhiteSpace(conversationId))
                conversationId = $"{platform}-{conversationPlatformId}";

            var actorId = $"nyxid-relay-{conversationId}";

            logger.LogInformation(
                "Relay message: platform={Platform}, conversation={ConversationId}, sender={Sender}",
                platform, conversationId, message.Sender?.DisplayName);

            // ─── Day One slash-command bridge ───
            // Intercept known and unknown slash commands deterministically so they do
            // not reach the LLM. Free-text still falls through to the actor/LLM path.
            // Bridge reply delivery uses the current callback token only — no long-term
            // state, no durable credential, no projection write.
            var dayOneBridge = http.RequestServices.GetService<INyxRelayDayOneBridge>();
            if (dayOneBridge is not null)
            {
                var bridgeRequest = new NyxRelayBridgeRequest(
                    Text: message.Content?.Text ?? string.Empty,
                    ConversationType: message.Conversation?.Type,
                    ConversationId: conversationId,
                    MessageId: message.MessageId,
                    Platform: message.Platform,
                    SenderId: message.Sender?.PlatformId,
                    SenderName: message.Sender?.DisplayName,
                    ScopeId: scopeId,
                    NyxIdAccessToken: userToken);

                if (dayOneBridge.ShouldHandle(bridgeRequest))
                {
                    // Dedupe at the Nyx-relay callback boundary so retried webhooks with the
                    // same message_id do not double-fire AgentBuilderTool side effects (a single
                    // `/daily` would otherwise create multiple agents + API keys on retry). The
                    // LLM fallback path relies on the actor's serialized mailbox for equivalent
                    // dedupe; the bridge skips the actor entirely, so it must use an atomic
                    // first-writer-wins guard here — a non-atomic TryGet+Set pair would let
                    // concurrent deliveries both observe the key as absent and both pass.
                    var idempotencyGuard = http.RequestServices.GetService<INyxRelayBridgeIdempotencyGuard>();
                    var dedupeKey = BuildBridgeDedupeKey(scopeId, message.MessageId!);
                    var firstDelivery = idempotencyGuard is null
                                        || idempotencyGuard.TryClaim(dedupeKey);
                    if (!firstDelivery)
                    {
                        logger.LogInformation(
                            "Relay Day One bridge duplicate callback suppressed: message_id={MessageId}, dedupe_key={DedupeKey}",
                            message.MessageId,
                            dedupeKey);
                        return Results.Accepted(value: new
                        {
                            status = "accepted",
                            bridge = "day_one_command",
                            dedupe = "duplicate",
                            message_id = message.MessageId,
                        });
                    }

                    logger.LogInformation(
                        "Relay Day One bridge owning message: platform={Platform}, conversation={ConversationId}, message_id={MessageId}",
                        platform, conversationId, message.MessageId);

                    _ = FinalizeDayOneBridgeReplyAsync(
                            dayOneBridge,
                            bridgeRequest,
                            userToken,
                            message.MessageId!,
                            nyxClient,
                            logger)
                        .ContinueWith(
                            task => logger.LogError(
                                task.Exception,
                                "Relay Day One bridge reply pipeline failed for message {MessageId}",
                                message.MessageId),
                            TaskContinuationOptions.OnlyOnFaulted);

                    return Results.Accepted(value: new
                    {
                        status = "accepted",
                        bridge = "day_one_command",
                        message_id = message.MessageId,
                    });
                }
            }

            var actor = await actorRuntime.GetAsync(actorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
            await actorStore.AddActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);

            var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var relayReply = new RelayReplyAccumulator(relayOptions.MaxBufferedResponseChars);
            var sessionId = !string.IsNullOrWhiteSpace(message.MessageId)
                ? $"{conversationId}-{message.MessageId}"
                : $"{conversationId}-{Guid.NewGuid():N}";

            var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                envelope =>
                {
                    var payload = envelope.Payload;
                    if (payload is null)
                        return Task.CompletedTask;

                    if (payload.Is(TextMessageContentEvent.Descriptor))
                    {
                        var evt = payload.Unpack<TextMessageContentEvent>();
                        if (!string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal))
                            return Task.CompletedTask;
                        relayReply.Append(evt.Delta);
                    }
                    else if (payload.Is(TextMessageEndEvent.Descriptor))
                    {
                        var evt = payload.Unpack<TextMessageEndEvent>();
                        if (!string.Equals(evt.SessionId, sessionId, StringComparison.Ordinal))
                            return Task.CompletedTask;

                        if (TryExtractLlmError(evt.Content, out var extractedError))
                        {
                            relayReply.SetError(extractedError);
                        }
                        else if (relayReply.IsEmpty && !string.IsNullOrWhiteSpace(evt.Content))
                        {
                            relayReply.Append(evt.Content);
                        }

                        responseTcs.TrySetResult(relayReply.Snapshot());
                    }

                    return Task.CompletedTask;
                },
                CancellationToken.None);

            var chatRequest = new ChatRequestEvent
            {
                Prompt = contentText,
                SessionId = sessionId,
                ScopeId = scopeId,
            };
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userToken;
            if (TryExtractRefreshToken(http) is { } refreshToken)
                chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdRefreshToken] = refreshToken;
            chatRequest.Metadata["scope_id"] = scopeId;
            chatRequest.Metadata["relay.platform"] = message.Platform ?? "";
            chatRequest.Metadata["relay.sender"] = message.Sender?.DisplayName ?? "";
            chatRequest.Metadata["relay.message_id"] = message.MessageId ?? "";
            await InjectUserConfigMetadataAsync(http, chatRequest.Metadata, ct);
            await InjectUserMemoryAsync(http, chatRequest.Metadata, ct);

            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(chatRequest),
                Route = new EnvelopeRoute
                {
                    Direct = new DirectRoute { TargetActorId = actor.Id },
                },
            };

            // Interactive reply collector scope must wrap the actor dispatch so that tool calls
            // fired inside the turn (e.g. reply_with_interaction) see an active AsyncLocal scope.
            // After dispatch returns we drain synchronously into a local and dispose the scope;
            // the captured intent is passed by value into the background FinalizeReplyAsync so
            // the post-dispatch lifetime of the scope does not matter.
            var interactiveReplyCollector = http.RequestServices.GetService<IInteractiveReplyCollector>();
            var interactiveReplyDispatcher = http.RequestServices.GetService<IInteractiveReplyDispatcher>();
            var relayChannel = ResolveRelayChannel(platform);
            MessageContent? capturedIntent = null;
            var replyScope = interactiveReplyCollector?.BeginScope();
            try
            {
                await actor.HandleEventAsync(envelope, ct);
                capturedIntent = interactiveReplyCollector?.TryTake();
            }
            finally
            {
                replyScope?.Dispose();
            }

            var configuration = http.RequestServices.GetService<IConfiguration>();
            _ = NyxIdRelayReplies.FinalizeReplyAsync(
                    subscription,
                    responseTcs,
                    relayReply,
                    sessionId,
                    message.MessageId!,
                    userToken,
                    chatRequest.Metadata,
                    relayOptions,
                    nyxClient,
                    configuration,
                    logger,
                    capturedIntent,
                    interactiveReplyDispatcher,
                    relayChannel)
                .ContinueWith(
                    task => logger.LogError(task.Exception, "Relay background reply pipeline failed for session {SessionId}", sessionId),
                    TaskContinuationOptions.OnlyOnFaulted);

            return Results.Accepted(value: new
            {
                status = "accepted",
                session_id = sessionId,
                message_id = message.MessageId,
            });
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Relay handler unexpected error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Classify a technical LLM error into a user-friendly message.</summary>
    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);

    private static bool TryExtractLlmError(string? content, out string error) =>
        NyxIdRelayReplies.TryExtractLlmError(content, out error);

    private static async Task<IResult?> TryHandleRelayWorkflowCardActionAsync(
        HttpContext http,
        RelayMessage message,
        ILogger logger,
        CancellationToken ct) =>
        await NyxIdRelayWorkflowCards.TryHandleAsync(http, message, logger, ct);

    private static string BuildBridgeDedupeKey(string scopeId, string messageId) =>
        $"nyxid-relay-bridge:{scopeId}:{messageId}";

    private static ChannelId ResolveRelayChannel(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform) ||
            string.Equals(platform, "unknown", StringComparison.OrdinalIgnoreCase))
            return ChannelId.From("unknown");

        var normalized = string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase)
            ? "lark"
            : platform.ToLowerInvariant();
        return ChannelId.From(normalized);
    }

    private static async Task FinalizeDayOneBridgeReplyAsync(
        INyxRelayDayOneBridge bridge,
        NyxRelayBridgeRequest request,
        string relayToken,
        string messageId,
        NyxIdApiClient nyxClient,
        ILogger logger)
    {
        string replyText;
        try
        {
            replyText = await bridge.HandleAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Relay Day One bridge handler threw: messageId={MessageId}",
                messageId);
            replyText = "Sorry, something went wrong while handling that command. Please try again.";
        }

        if (string.IsNullOrWhiteSpace(replyText))
            replyText = "Sorry, I wasn't able to generate a response.";

        using var deliveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delivery = await nyxClient.SendChannelRelayTextReplyAsync(
            relayToken,
            messageId,
            replyText,
            deliveryCts.Token);
        if (!delivery.Succeeded)
        {
            logger.LogError(
                "Relay Day One bridge reply delivery failed: messageId={MessageId}, detail={Detail}",
                messageId,
                delivery.Detail);
            return;
        }

        logger.LogInformation(
            "Relay Day One bridge reply delivered: messageId={MessageId}, platformMessageId={PlatformMessageId}",
            delivery.MessageId,
            delivery.PlatformMessageId);
    }

    private static string? NormalizeOptional(string? value) =>
        NyxIdRelayPayloads.NormalizeOptional(value);

    private sealed class RelayReplyAccumulator : NyxIdRelayReplyAccumulator
    {
        public RelayReplyAccumulator(int maxChars) : base(maxChars)
        {
        }
    }
}
