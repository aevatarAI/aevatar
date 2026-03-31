using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Controllers;

public static class ChatHistoryEndpoints
{
    public static IEndpointRouteBuilder MapChatHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes/{scopeId}/chat-history");
        group.MapGet("", HandleGetIndex);
        group.MapGet("/conversations/{conversationId}", HandleGetConversation);
        group.MapPut("/conversations/{conversationId}", HandleSaveConversation);
        group.MapDelete("/conversations/{conversationId}", HandleDeleteConversation);
        return app;
    }

    private static async Task<IResult> HandleGetIndex(
        string scopeId,
        [FromServices] IChatHistoryStore store,
        CancellationToken ct)
    {
        var index = await store.GetIndexAsync(scopeId, ct);
        return Results.Ok(index);
    }

    private static async Task<IResult> HandleGetConversation(
        string scopeId,
        string conversationId,
        [FromServices] IChatHistoryStore store,
        CancellationToken ct)
    {
        var messages = await store.GetMessagesAsync(scopeId, conversationId, ct);
        return Results.Ok(messages);
    }

    private static async Task<IResult> HandleSaveConversation(
        string scopeId,
        string conversationId,
        SaveConversationRequest request,
        [FromServices] IChatHistoryStore store,
        CancellationToken ct)
    {
        await store.SaveMessagesAsync(scopeId, conversationId, request.Messages, ct);

        var currentIndex = await store.GetIndexAsync(scopeId, ct);
        var conversations = currentIndex.Conversations
            .Where(c => !string.Equals(c.Id, conversationId, StringComparison.Ordinal))
            .Append(request.Meta)
            .OrderByDescending(c => c.UpdatedAt)
            .ToList();

        await store.SaveIndexAsync(scopeId, new ChatHistoryIndex(conversations), ct);
        return Results.Ok();
    }

    private static async Task<IResult> HandleDeleteConversation(
        string scopeId,
        string conversationId,
        [FromServices] IChatHistoryStore store,
        CancellationToken ct)
    {
        await store.DeleteConversationAsync(scopeId, conversationId, ct);
        return Results.Ok();
    }

    public sealed record SaveConversationRequest(
        ConversationMeta Meta,
        IReadOnlyList<StoredChatMessage> Messages);
}
