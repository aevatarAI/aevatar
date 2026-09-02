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
        group.MapGet("/create-recovery/{commandId}", HandleGetCreateRecovery);
        group.MapGet("/conversations/{conversationId}", HandleGetConversation);
        group.MapDelete("/conversations/{conversationId}", HandleDeleteConversation);
        return app;
    }

    private static async Task<IResult> HandleGetIndex(
        HttpContext http,
        string scopeId,
        [FromQuery] int? pageSize,
        [FromQuery] string? cursor,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var index = await queryPort.GetIndexAsync(
            new ChatHistoryIndexPageRequest(scopeId, pageSize ?? 50, cursor),
            ct);
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
        return messages.Status == ChatHistoryConversationResultStatus.NotFound
            ? Results.NotFound()
            : Results.Ok(new ChatHistoryConversationResponse(
                messages.Messages,
                messages.StateVersion));
    }

    private static async Task<IResult> HandleGetCreateRecovery(
        HttpContext http,
        string scopeId,
        string commandId,
        [FromServices] IChatHistoryQueryPort queryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var recovery = await queryPort.GetCreateRecoveryAsync(scopeId, commandId, ct);
        return recovery.Status == ChatHistoryCreateRecoveryStatus.NotFound
            ? Results.NotFound()
            : Results.Ok(ToCreateRecoveryResponse(recovery));
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

        var result = await commandPort.DeleteConversationAsync(scopeId, conversationId, ct);
        return result.Status == ChatHistoryDeleteResultStatus.NotFound
            ? Results.NotFound()
            : Results.Ok();
    }

    private static ChatHistoryCreateRecoveryResponse ToCreateRecoveryResponse(
        ChatHistoryCreateRecoveryResult result) =>
        new(
            ToCreateRecoveryStatusName(result.Status),
            result.ScopeId,
            result.CommandId,
            result.ConversationId,
            result.TurnId,
            result.WorkflowActorId,
            result.WorkflowCommandId,
            result.WorkflowCorrelationId,
            result.RequestFingerprint,
            result.StateVersion,
            result.UpdatedAt);

    private static string ToCreateRecoveryStatusName(ChatHistoryCreateRecoveryStatus status) =>
        status switch
        {
            ChatHistoryCreateRecoveryStatus.Reserved => "reserved",
            ChatHistoryCreateRecoveryStatus.Bound => "bound",
            ChatHistoryCreateRecoveryStatus.AppendDispatched => "append_dispatched",
            ChatHistoryCreateRecoveryStatus.Abandoned => "abandoned",
            ChatHistoryCreateRecoveryStatus.Failed => "failed",
            ChatHistoryCreateRecoveryStatus.AppendCommitted => "append_committed",
            ChatHistoryCreateRecoveryStatus.AppendRejected => "append_rejected",
            _ => "not_found",
        };

    private sealed record ChatHistoryCreateRecoveryResponse(
        string Status,
        string ScopeId,
        string CommandId,
        string? ConversationId,
        string? TurnId,
        string? WorkflowActorId,
        string? WorkflowCommandId,
        string? WorkflowCorrelationId,
        string? RequestFingerprint,
        long StateVersion,
        DateTimeOffset UpdatedAt);

    private sealed record ChatHistoryConversationResponse(
        IReadOnlyList<StoredChatMessage> Messages,
        long StateVersion);
}
