namespace Aevatar.AI.ToolProviders.Lark;

public interface ILarkNyxClient
{
    Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct);
    Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct);
}

public sealed record LarkSendMessageRequest(
    string TargetType,
    string TargetId,
    string MessageType,
    string ContentJson,
    string? IdempotencyKey = null);

public sealed record LarkChatSearchRequest(
    string? Query,
    IReadOnlyList<string>? MemberIds,
    IReadOnlyList<string>? SearchTypes,
    bool IsManager,
    bool DisableSearchByUser,
    int PageSize,
    string? PageToken);
