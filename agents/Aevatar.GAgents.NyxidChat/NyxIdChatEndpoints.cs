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
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
                actor.Id,
                async envelope =>
                {
                    try
                    {
                        await MapAndWriteEventAsync(envelope, messageId, writer);

                        // Detect completion
                        if (envelope.Payload is not null && envelope.Payload.Is(TextMessageEndEvent.Descriptor))
                        {
                            tcs.TrySetResult();
                        }
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
                await writer.WriteRunFinishedAsync(CancellationToken.None);
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
    private static async ValueTask MapAndWriteEventAsync(
        EventEnvelope envelope,
        string messageId,
        NyxIdChatSseWriter writer)
    {
        var payload = envelope.Payload;
        if (payload is null)
            return;

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
            await writer.WriteToolCallAsync(evt.ToolName, evt.CallId, CancellationToken.None);
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
                    return;
                }

                if (evt.Content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
                {
                    await writer.WriteRunErrorAsync(evt.Content.Trim(), CancellationToken.None);
                    return;
                }
            }

            await writer.WriteTextEndAsync(messageId, CancellationToken.None);
        }
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
}
