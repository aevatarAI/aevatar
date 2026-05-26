namespace Aevatar.AI.ToolProviders.Lark;

public interface ILarkNyxClient
{
    Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct);
    Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct);
    Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct);
    Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct);
    Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct);
    Task<string> SearchMessagesAsync(string token, LarkMessageSearchRequest request, CancellationToken ct);
    Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct);
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

public sealed record LarkReplyMessageRequest(
    string MessageId,
    string MessageType,
    string ContentJson,
    bool ReplyInThread,
    string? IdempotencyKey = null);

public sealed record LarkMessageReactionRequest(
    string MessageId,
    string EmojiType);

public sealed record LarkMessageReactionListRequest(
    string MessageId,
    string? EmojiType,
    int PageSize,
    string? PageToken,
    string? UserIdType);

public sealed record LarkMessageReactionDeleteRequest(
    string MessageId,
    string ReactionId);

public sealed record LarkMessageSearchRequest(
    string Query,
    IReadOnlyList<string>? ChatIds,
    IReadOnlyList<string>? SenderIds,
    string? IncludeAttachmentType,
    string? ChatType,
    string? SenderType,
    string? ExcludeSenderType,
    bool IsAtMe,
    string? StartTime,
    string? EndTime,
    int PageSize,
    string? PageToken);

public sealed record LarkMessagesBatchGetRequest(
    IReadOnlyList<string> MessageIds);

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
