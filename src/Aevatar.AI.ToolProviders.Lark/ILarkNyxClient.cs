namespace Aevatar.AI.ToolProviders.Lark;

public interface ILarkNyxClient
{
    Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct);
    Task<string> ReplyToMessageAsync(string token, LarkReplyMessageRequest request, CancellationToken ct);
    Task<string> CreateMessageReactionAsync(string token, LarkMessageReactionRequest request, CancellationToken ct);
    Task<string> ListMessageReactionsAsync(string token, LarkMessageReactionListRequest request, CancellationToken ct);
    Task<string> DeleteMessageReactionAsync(string token, LarkMessageReactionDeleteRequest request, CancellationToken ct);
    Task<string> BatchGetMessagesAsync(string token, LarkMessagesBatchGetRequest request, CancellationToken ct);
    Task<LarkMessageResourceDownloadResult> DownloadMessageResourceAsync(
        string token,
        LarkMessageResourceDownloadRequest request,
        CancellationToken ct);
    Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct);
    Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct);
    Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct);
    Task<string> GetApprovalInstanceAsync(string token, LarkApprovalInstanceGetRequest request, CancellationToken ct);
    Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct);
    Task<string> CreateDocxDocumentAsync(string token, LarkDocxCreateRequest request, CancellationToken ct);
    Task<string> AppendDocxTextBlocksAsync(string token, LarkDocxAppendBlocksRequest request, CancellationToken ct);
    Task<string> SetDrivePermissionAsync(string token, LarkDrivePermissionRequest request, CancellationToken ct);
    Task<string> CreateBitableAppAsync(string token, LarkBitableCreateRequest request, CancellationToken ct);
    Task<string> GrantResourceMemberAsync(string token, LarkResourceMemberGrantRequest request, CancellationToken ct);
    Task<string> UploadDriveMediaAsync(string token, LarkDriveMediaUploadRequest request, CancellationToken ct);
    Task<string> UploadApprovalFileAsync(string token, LarkApprovalFileUploadRequest request, CancellationToken ct);
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

public sealed record LarkMessagesBatchGetRequest(
    IReadOnlyList<string> MessageIds);

public enum LarkMessageResourceKind
{
    Image,
    File,
}

public sealed record LarkMessageResourceDownloadRequest(
    string MessageId,
    string ResourceKey,
    LarkMessageResourceKind Kind);

public sealed record LarkMessageResourceDownloadResult(
    bool Succeeded,
    byte[] Content,
    string? ContentType = null,
    string? FileName = null,
    string? Detail = null,
    int HttpStatus = 0);

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

/// <summary>
/// Query for Lark <c>GET /open-apis/approval/v4/tasks/query</c>. The official contract
/// requires both <paramref name="UserId"/> and <paramref name="Topic"/>; <paramref name="UserIdType"/>
/// must describe the id type of <paramref name="UserId"/> (defaults to <c>open_id</c> on the Lark side).
/// </summary>
public sealed record LarkApprovalTaskQueryRequest(
    string Topic,
    string UserId,
    int PageSize,
    string? PageToken,
    string? UserIdType);

public sealed record LarkApprovalInstanceGetRequest(
    string InstanceCode,
    string? Locale,
    string? UserIdType);

/// <summary>
/// Action against Lark <c>POST /open-apis/approval/v4/tasks/approve|reject|transfer</c>.
/// All three endpoints require <paramref name="ApprovalCode"/> (the approval definition code)
/// in addition to <paramref name="InstanceCode"/> + <paramref name="TaskId"/> + <paramref name="UserId"/>;
/// <paramref name="UserIdType"/> rides as a query parameter and must match the id type of
/// <paramref name="UserId"/> (and <paramref name="TransferUserId"/> for transfer).
/// </summary>
public sealed record LarkApprovalTaskActionRequest(
    string Action,
    string ApprovalCode,
    string InstanceCode,
    string TaskId,
    string UserId,
    string? Comment,
    string? FormJson,
    string? TransferUserId,
    string? UserIdType);

public enum LarkDocxVisibility
{
    Readable,
    Editable,
}

public sealed record LarkDocxCreateRequest(
    string Title);

public sealed record LarkDocxAppendBlocksRequest(
    string DocumentId,
    string MarkdownText);

public sealed record LarkDrivePermissionRequest(
    string DocumentToken,
    LarkDocxVisibility Visibility,
    string? ReceiveId,
    string? ReceiveIdType,
    string ObjType = "docx");

/// <summary>Create a Lark Bitable app (多维表格). <c>POST /open-apis/bitable/v1/apps</c>.</summary>
public sealed record LarkBitableCreateRequest(
    string Name,
    string? FolderToken = null);

/// <summary>
/// Grant a single member access to a Drive resource via
/// <c>POST /open-apis/drive/v1/permissions/{token}/members?type={ObjType}</c>.
/// <paramref name="MemberType"/> is the Lark id kind (e.g. <c>openid</c>); <paramref name="Perm"/> is
/// <c>view | edit | full_access</c>.
/// </summary>
public sealed record LarkResourceMemberGrantRequest(
    string Token,
    string ObjType,
    string MemberId,
    string MemberType = "openid",
    string Perm = "full_access",
    bool NeedNotification = false);

public sealed record LarkDriveMediaUploadRequest(
    string FileName,
    string ParentType,
    string ParentNode,
    long Size,
    string ContentType,
    Stream Content,
    string? Checksum = null,
    string? Extra = null);

public sealed record LarkApprovalFileUploadRequest(
    string FileName,
    string FileType,
    long Size,
    string ContentType,
    Stream Content);
