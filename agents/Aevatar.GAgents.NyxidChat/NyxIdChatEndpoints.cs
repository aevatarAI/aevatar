using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatEndpoints
{
    public static IEndpointRouteBuilder MapNyxIdChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("NyxIdChat");
        group.MapPost("/{scopeId}/nyxid-chat/conversations", HandleCreateConversationAsync);
        group.MapGet("/{scopeId}/nyxid-chat/conversations", HandleListConversationsAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:stream", HandleStreamMessageAsync);
        group.MapDelete("/{scopeId}/nyxid-chat/conversations/{actorId}", HandleDeleteConversationAsync);

        // NyxID Channel Bot Relay webhook — receives forwarded platform messages
        app.MapPost("/api/webhooks/nyxid-relay", HandleRelayWebhookAsync).WithTags("NyxIdRelay");

        return app;
    }

    private static async Task<IResult> HandleCreateConversationAsync(
        HttpContext http,
        string scopeId,
        [FromServices] NyxIdChatActorStore actorStore,
        CancellationToken ct)
    {
        var entry = await actorStore.CreateActorAsync(scopeId, ct);
        return Results.Ok(new { actorId = entry.ActorId, createdAt = entry.CreatedAt });
    }

    private static async Task<IResult> HandleListConversationsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] NyxIdChatActorStore actorStore,
        CancellationToken ct)
    {
        var actors = await actorStore.ListActorsAsync(scopeId, ct);
        return Results.Ok(actors);
    }

    private static async Task HandleStreamMessageAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        NyxIdChatStreamRequest request,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var writer = new NyxIdChatSseWriter(http.Response);

        try
        {
            var accessToken = ExtractBearerToken(http);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // Get or create the actor
            var actor = await actorRuntime.GetAsync(actorId)
                        ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);

            // Set up SSE response
            await writer.StartAsync(ct);

            var messageId = Guid.NewGuid().ToString("N");
            await writer.WriteRunStartedAsync(actorId, ct);

            // Subscribe to actor events and map to SSE frames
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope =>
                {
                    try
                    {
                        var terminalFrame = await MapAndWriteEventAsync(envelope, messageId, writer);
                        if (!string.IsNullOrWhiteSpace(terminalFrame))
                            tcs.TrySetResult(terminalFrame);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                },
                ct);

            // Build and dispatch ChatRequestEvent to the actor
            var chatRequest = new ChatRequestEvent
            {
                Prompt = prompt,
                SessionId = request.SessionId ?? messageId,
                ScopeId = scopeId,
            };
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken;
            chatRequest.Metadata["scope_id"] = scopeId;
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

            await actor.HandleEventAsync(envelope, ct);

            // Wait for completion or timeout
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(120_000, ct));

            if (completedTask == tcs.Task)
            {
                if (tcs.Task.IsFaulted)
                {
                    var ex = tcs.Task.Exception?.InnerException ?? tcs.Task.Exception;
                    await writer.WriteRunErrorAsync(ex?.Message ?? "An error occurred.", CancellationToken.None);
                }
                else if (string.Equals(tcs.Task.Result, "TEXT_MESSAGE_END", StringComparison.Ordinal))
                {
                    await writer.WriteRunFinishedAsync(CancellationToken.None);
                }
            }
            else
            {
                await writer.WriteRunErrorAsync("Request timed out.", CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NyxID chat stream failed for actor {ActorId}", actorId);
            if (!writer.Started)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await writer.WriteRunErrorAsync(ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Maps AI event envelope payloads to NyxIdChat SSE frames.
    /// </summary>
    private static async ValueTask<string?> MapAndWriteEventAsync(
        EventEnvelope envelope,
        string messageId,
        NyxIdChatSseWriter writer)
    {
        var payload = envelope.Payload;
        if (payload is null)
            return null;

        if (payload.Is(TextMessageStartEvent.Descriptor))
        {
            await writer.WriteTextStartAsync(messageId, CancellationToken.None);
        }
        else if (payload.Is(TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageContentEvent>();
            if (!string.IsNullOrEmpty(evt.Delta))
                await writer.WriteTextDeltaAsync(evt.Delta, CancellationToken.None);
        }
        else if (payload.Is(ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolCallEvent>();
            await writer.WriteToolCallStartAsync(evt.ToolName, evt.CallId, CancellationToken.None);
        }
        else if (payload.Is(ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolResultEvent>();
            await writer.WriteToolCallEndAsync(evt.CallId, evt.ResultJson, CancellationToken.None);
        }
        else if (payload.Is(ToolApprovalRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolApprovalRequestEvent>();
            await writer.WriteToolApprovalRequestAsync(
                evt.RequestId, evt.ToolName, evt.ToolCallId,
                evt.ArgumentsJson, evt.IsDestructive, evt.TimeoutSeconds,
                CancellationToken.None);
        }
        else if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();

            // Check for LLM error markers
            if (!string.IsNullOrEmpty(evt.Content))
            {
                const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
                const string llmFailedPrefix = "LLM request failed:";
                if (evt.Content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
                {
                    await writer.WriteRunErrorAsync(
                        evt.Content[llmErrorPrefix.Length..].Trim(), CancellationToken.None);
                    return "RUN_ERROR";
                }

                if (evt.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                {
                    await writer.WriteRunErrorAsync(evt.Content.Trim(), CancellationToken.None);
                    return "RUN_ERROR";
                }
            }

            await writer.WriteTextEndAsync(messageId, CancellationToken.None);
            return "TEXT_MESSAGE_END";
        }

        return null;
    }

    private static async Task<IResult> HandleDeleteConversationAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] NyxIdChatActorStore actorStore,
        CancellationToken ct)
    {
        var removed = await actorStore.DeleteActorAsync(scopeId, actorId, ct);
        return removed ? Results.Ok() : Results.NotFound();
    }

    private static async Task InjectUserConfigMetadataAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var preferencesStore = http.RequestServices.GetService<INyxIdUserLlmPreferencesStore>();
        if (preferencesStore == null)
            return;

        try
        {
            var preferences = await preferencesStore.GetAsync(ct);
            if (!string.IsNullOrWhiteSpace(preferences.DefaultModel))
                metadata[LLMRequestMetadataKeys.ModelOverride] = preferences.DefaultModel.Trim();
            if (!string.IsNullOrWhiteSpace(preferences.PreferredRoute))
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = preferences.PreferredRoute.Trim();
            if (preferences.MaxToolRounds > 0)
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] = preferences.MaxToolRounds.ToString();
        }
        catch
        {
            // Best-effort
        }
    }

    private static async Task InjectUserMemoryAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var memoryStore = http.RequestServices.GetService<IUserMemoryStore>();
        if (memoryStore == null)
            return;

        try
        {
            var section = await memoryStore.BuildPromptSectionAsync(2000, ct);
            if (!string.IsNullOrWhiteSpace(section))
                metadata[LLMRequestMetadataKeys.UserMemoryPrompt] = section;
        }
        catch
        {
            // Best-effort
        }
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        return null;
    }

    public sealed record NyxIdChatStreamRequest(string? Prompt, string? SessionId = null);

    // ─── NyxID Channel Bot Relay ───

    /// <summary>
    /// Receives forwarded platform messages from NyxID Channel Bot Relay.
    /// Verifies HMAC signature, dispatches to NyxIdChat actor, collects response, returns sync reply.
    /// </summary>
    private static async Task<IResult> HandleRelayWebhookAsync(
        HttpContext http,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] NyxIdChatActorStore actorStore,
        [FromServices] NyxIdRelayOptions relayOptions,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Relay");

        // Read body for signature verification and parsing
        http.Request.EnableBuffering();
        var bodyBytes = await ReadRequestBodyAsync(http.Request, ct);
        var bodyString = Encoding.UTF8.GetString(bodyBytes);

        // Verify HMAC signature if webhook secret is configured
        if (!string.IsNullOrWhiteSpace(relayOptions.WebhookSecret))
        {
            var signature = http.Request.Headers["X-NyxID-Signature"].FirstOrDefault();
            var timestamp = http.Request.Headers["X-NyxID-Timestamp"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature))
                return Results.Json(new { error = "Missing X-NyxID-Signature header" }, statusCode: 401);

            var signPayload = string.IsNullOrWhiteSpace(timestamp)
                ? bodyString
                : $"{timestamp}.{bodyString}";

            if (!VerifyHmacSignature(relayOptions.WebhookSecret, signPayload, signature))
            {
                logger.LogWarning("Relay webhook signature verification failed");
                return Results.Json(new { error = "Invalid signature" }, statusCode: 401);
            }
        }

        // Parse the relay payload
        RelayMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<RelayMessage>(bodyString, RelayJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse relay payload");
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Content?.Text))
            return Results.Json(new { error = "Empty message content" }, statusCode: 400);

        // Extract user token from NyxID headers
        var userToken = http.Request.Headers["X-NyxID-User-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userToken))
            return Results.Json(new { error = "Missing X-NyxID-User-Token header" }, statusCode: 401);

        // Resolve scope and conversation actor
        var scopeId = message.Agent?.ApiKeyId ?? "default";
        var conversationId = message.Conversation?.Id;
        if (string.IsNullOrWhiteSpace(conversationId))
            conversationId = $"{message.Platform}-{message.Conversation?.PlatformId ?? "unknown"}";

        var actorId = $"nyxid-relay-{conversationId}";

        logger.LogInformation(
            "Relay message: platform={Platform}, conversation={ConversationId}, sender={Sender}",
            message.Platform, conversationId, message.Sender?.DisplayName);

        // Get or create actor
        var actor = await actorRuntime.GetAsync(actorId)
                    ?? await actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);

        // Ensure conversation is tracked in store
        await actorStore.EnsureActorAsync(scopeId, actorId, ct);

        // Subscribe to actor events and collect response
        var responseTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseBuilder = new StringBuilder();
        using var ctr = ct.Register(() => responseTcs.TrySetCanceled());

        await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
            actor.Id,
            envelope =>
            {
                var payload = envelope.Payload;
                if (payload is null) return Task.CompletedTask;

                if (payload.Is(TextMessageContentEvent.Descriptor))
                {
                    var evt = payload.Unpack<TextMessageContentEvent>();
                    if (!string.IsNullOrEmpty(evt.Delta))
                        responseBuilder.Append(evt.Delta);
                }
                else if (payload.Is(TextMessageEndEvent.Descriptor))
                {
                    responseTcs.TrySetResult(responseBuilder.ToString());
                }

                return Task.CompletedTask;
            },
            ct);

        // Build and dispatch ChatRequestEvent
        var chatRequest = new ChatRequestEvent
        {
            Prompt = message.Content.Text,
            SessionId = conversationId,
            ScopeId = scopeId,
        };
        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = userToken;
        chatRequest.Metadata["scope_id"] = scopeId;
        chatRequest.Metadata["relay.platform"] = message.Platform ?? "";
        chatRequest.Metadata["relay.sender"] = message.Sender?.DisplayName ?? "";
        chatRequest.Metadata["relay.message_id"] = message.MessageId ?? "";

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

        await actor.HandleEventAsync(envelope, ct);

        // Wait for response
        var timeoutMs = relayOptions.ResponseTimeoutSeconds * 1000;
        var completed = await Task.WhenAny(responseTcs.Task, Task.Delay(timeoutMs, ct));

        string replyText;
        if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
        {
            replyText = responseTcs.Task.Result;
        }
        else
        {
            replyText = responseBuilder.Length > 0
                ? responseBuilder.ToString() // partial response is better than nothing
                : "Sorry, the request timed out. Please try again.";
        }

        return Results.Json(new { reply = new { text = replyText } });
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        request.Body.Position = 0;
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static bool VerifyHmacSignature(string secret, string payload, string expectedSignature)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hashBytes = HMACSHA256.HashData(keyBytes, payloadBytes);
        var computed = Convert.ToHexStringLower(hashBytes);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expectedSignature.ToLowerInvariant()));
    }

    // ─── Relay payload models ───

    private static readonly JsonSerializerOptions RelayJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class RelayMessage
    {
        public string? MessageId { get; set; }
        public string? Platform { get; set; }
        public RelayAgent? Agent { get; set; }
        public RelayConversation? Conversation { get; set; }
        public RelaySender? Sender { get; set; }
        public RelayContent? Content { get; set; }
        public string? Timestamp { get; set; }
    }

    private sealed class RelayAgent
    {
        public string? ApiKeyId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class RelayConversation
    {
        public string? Id { get; set; }
        public string? PlatformId { get; set; }
        public string? Type { get; set; }
    }

    private sealed class RelaySender
    {
        public string? PlatformId { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class RelayContent
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }
}
