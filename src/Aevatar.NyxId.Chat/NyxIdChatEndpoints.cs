using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.NyxId.Chat;

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
        [FromServices] ILLMProviderFactory providerFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Aevatar.NyxId.Chat.Endpoints");
        var writer = new NyxIdChatSseWriter(http.Response);

        try
        {
            var provider = providerFactory.GetProvider("nyxid");

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

            var messageId = Guid.NewGuid().ToString("N");
            var llmRequest = new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System(NyxIdChatSystemPrompt.Value),
                    ChatMessage.User(prompt),
                ],
                Metadata = new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = accessToken,
                },
            };

            await writer.WriteRunStartedAsync(actorId, ct);
            await writer.WriteTextStartAsync(messageId, ct);

            await foreach (var chunk in provider.ChatStreamAsync(llmRequest, ct))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    await writer.WriteTextDeltaAsync(chunk.DeltaContent, ct);
                }
            }

            await writer.WriteTextEndAsync(messageId, ct);
            await writer.WriteRunFinishedAsync(ct);
        }
        catch (OperationCanceledException)
        {
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
