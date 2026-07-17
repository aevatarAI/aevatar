using Aevatar.Capabilities;
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
        HttpContext http,
        string scopeId,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var index = await queryPort.GetIndexAsync(scopeId, ct);
        return Results.Ok(index);
    }

    private static async Task<IResult> HandleGetConversation(
        HttpContext http,
        string scopeId,
        string conversationId,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var messages = await queryPort.GetMessagesAsync(scopeId, conversationId, ct);
        return Results.Ok(messages);
    }

    private static async Task<IResult> HandleSaveConversation(
        HttpContext http,
        string scopeId,
        string conversationId,
        SaveConversationRequest request,
        [FromServices] IChatHistoryCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        await commandPort.SaveMessagesAsync(scopeId, conversationId, request.Meta, request.Messages, ct);
        return Results.Ok();
    }

    private static async Task<IResult> HandleDeleteConversation(
        HttpContext http,
        string scopeId,
        string conversationId,
        [FromServices] IChatHistoryCommandPort commandPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        await commandPort.DeleteConversationAsync(scopeId, conversationId, ct);
        return Results.Ok();
    }

    public sealed record SaveConversationRequest(
        ConversationMeta Meta,
        IReadOnlyList<StoredChatMessage> Messages);
}
