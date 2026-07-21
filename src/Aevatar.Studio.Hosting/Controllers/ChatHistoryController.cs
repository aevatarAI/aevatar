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
        group.MapGet("/create-recoveries/{createIdempotencyKey}", HandleGetCreateRecovery);
        group.MapGet("/conversations/{conversationId}", HandleGetConversation);
        group.MapDelete("/conversations/{conversationId}", HandleDeleteConversation);
        return app;
    }

    private static async Task<IResult> HandleGetIndex(
        HttpContext http,
        string scopeId,
        [FromQuery] int? take,
        [FromQuery] string? cursor,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var index = await queryPort.GetIndexAsync(new ChatHistoryPageRequest(scopeId, take, cursor), ct);
        return Results.Ok(index);
    }

    private static async Task<IResult> HandleGetCreateRecovery(
        HttpContext http,
        string scopeId,
        string createIdempotencyKey,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var recovery = await queryPort.GetCreateRecoveryAsync(
            new ChatCreateRecoveryRequest(scopeId, createIdempotencyKey),
            ct);
        return recovery is null ? Results.NotFound() : Results.Ok(recovery);
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
}
