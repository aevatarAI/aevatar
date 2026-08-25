namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IChatHistoryQueryPort
{
    Task<ChatHistoryIndexPage> GetIndexAsync(
        ChatHistoryIndexPageRequest request,
        CancellationToken ct = default);

    Task<ChatHistoryConversationMessagesResult> GetMessagesAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);

    Task<ChatHistoryCreateRecoveryResult> GetCreateRecoveryAsync(
        string scopeId,
        string commandId,
        CancellationToken ct = default);
}

public sealed record ChatHistoryIndex(IReadOnlyList<ConversationMeta> Conversations);

public sealed record ChatHistoryIndexPageRequest(
    string ScopeId,
    int PageSize = 50,
    string? Cursor = null);

public sealed record ChatHistoryIndexPage(
    IReadOnlyList<ConversationMeta> Conversations,
    string? NextCursor);

public enum ChatHistoryConversationResultStatus
{
    Found = 0,
    NotFound = 1,
}

public enum ChatHistoryConversationProjectionStatus
{
    Current = 0,
    Pending = 1,
}

public sealed record ChatHistoryConversationMessagesResult(
    ChatHistoryConversationResultStatus Status,
    IReadOnlyList<StoredChatMessage> Messages,
    long StateVersion,
    ChatHistoryConversationProjectionStatus ProjectionStatus,
    IReadOnlyList<StoredChatTurnOperation> Operations)
{
    public static ChatHistoryConversationMessagesResult Found(
        IReadOnlyList<StoredChatMessage> messages) =>
        Found(messages, 0);

    public static ChatHistoryConversationMessagesResult Found(
        IReadOnlyList<StoredChatMessage> messages,
        long stateVersion) =>
        Found(messages, stateVersion, []);

    public static ChatHistoryConversationMessagesResult Found(
        IReadOnlyList<StoredChatMessage> messages,
        long stateVersion,
        IReadOnlyList<StoredChatTurnOperation> operations) =>
        new(
            ChatHistoryConversationResultStatus.Found,
            messages,
            Math.Max(0, stateVersion),
            ChatHistoryConversationProjectionStatus.Current,
            operations);

    public static ChatHistoryConversationMessagesResult Pending() =>
        new(
            ChatHistoryConversationResultStatus.Found,
            [],
            0,
            ChatHistoryConversationProjectionStatus.Pending,
            []);

    public static ChatHistoryConversationMessagesResult NotFound() =>
        new(
            ChatHistoryConversationResultStatus.NotFound,
            [],
            0,
            ChatHistoryConversationProjectionStatus.Current,
            []);
}

/// <summary>
/// One stored Model or Tool operation of a conversation turn.
/// </summary>
/// <remarks>
/// This is the durable copy of the trajectory ledger. Timing is null when the
/// operation never reported it, and previews are sanitized fragments whenever
/// <see cref="PreviewsTruncated"/> is set. Tool result bodies are absent by
/// design: untrusted external text is not retained by the conversation actor.
/// </remarks>
public sealed record StoredChatTurnOperation(
    string TurnId,
    string OperationId,
    int Order,
    string Kind,
    string Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Model = null,
    string? Provider = null,
    string? FinishReason = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    string? InputPreview = null,
    string? OutputPreview = null,
    string? ArgumentsPreview = null,
    bool PreviewsTruncated = false,
    string? SafeMessage = null,
    IReadOnlyList<string>? AvailableToolNames = null,
    bool ToolCatalogCaptured = false);

public enum ChatHistoryCreateRecoveryStatus
{
    NotFound = 0,
    Reserved = 1,
    Bound = 2,
    AppendDispatched = 3,
    Abandoned = 4,
    Failed = 5,
    AppendCommitted = 6,
    AppendRejected = 7,
    TerminalReconciliationPrepared = 8,
}

public sealed record ChatHistoryCreateRecoveryResult(
    ChatHistoryCreateRecoveryStatus Status,
    string ScopeId,
    string CommandId,
    string? ConversationId,
    string? TurnId,
    string? WorkflowActorId,
    string? WorkflowCommandId,
    string? WorkflowCorrelationId,
    string? RequestFingerprint,
    long StateVersion,
    DateTimeOffset UpdatedAt)
{
    public static ChatHistoryCreateRecoveryResult NotFound(string scopeId, string commandId) =>
        new(
            ChatHistoryCreateRecoveryStatus.NotFound,
            scopeId,
            commandId,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            DateTimeOffset.UnixEpoch);
}

public sealed record ConversationMeta(
    string Id,
    string Title,
    string ServiceId,
    string ServiceKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LlmRoute = null,
    string? LlmModel = null,
    string? TaskStatus = null,
    string? AttentionKind = null,
    DateTimeOffset? AttentionSince = null,
    string? ActiveStepSummary = null,
    long StateVersion = 0);

public sealed record StoredChatMessage(
    string Id,
    string Role,
    string Content,
    long Timestamp,
    string Status,
    string? Error = null,
    string? Thinking = null,
    string? AuthorId = null,
    string? AuthorName = null,
    string? TurnId = null);
