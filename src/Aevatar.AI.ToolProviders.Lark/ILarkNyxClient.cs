namespace Aevatar.AI.ToolProviders.Lark;

public interface ILarkNyxClient
{
    Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct);
    Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct);
    Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct);
    Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct);
    Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct);
    Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct);
}

public sealed record LarkSendMessageRequest(
    string TargetType,
    string TargetId,
    string MessageType,
    string ContentJson,
    string? IdempotencyKey = null);

public sealed record LarkMessageReactionRequest(
    string MessageId,
    string EmojiType);

public sealed record LarkChatSearchRequest(
    string? Query,
    IReadOnlyList<string>? MemberIds,
    IReadOnlyList<string>? SearchTypes,
    bool IsManager,
    bool DisableSearchByUser,
    int PageSize,
    string? PageToken);

public sealed record LarkSheetAppendRowsRequest(
    string SpreadsheetToken,
    string Range,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

public sealed record LarkApprovalTaskQueryRequest(
    string Topic,
    string? DefinitionCode,
    string? Locale,
    int PageSize,
    string? PageToken,
    string? UserIdType);

public sealed record LarkApprovalTaskActionRequest(
    string Action,
    string InstanceCode,
    string TaskId,
    string? Comment,
    string? FormJson,
    string? TransferUserId,
    string? UserIdType);
