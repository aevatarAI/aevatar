namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IChatHistoryCommandPort
{
    Task InitializeConversationAsync(
        ChatHistoryConversationInitialization request,
        CancellationToken ct = default);

    Task ReserveTurnDeliveryAsync(
        ChatHistoryTurnDeliveryReservation request,
        CancellationToken ct = default);

    Task NotifyTurnTerminalAsync(
        ChatHistoryTurnTerminalNotification notification,
        CancellationToken ct = default);

    Task SaveMessagesAsync(
        string scopeId,
        string conversationId,
        ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages,
        CancellationToken ct = default);

    Task<ChatHistoryDeleteResult> DeleteConversationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);
}

public sealed record ChatHistoryConversationInitialization(
    string OperationId,
    string ScopeId,
    string ConversationId,
    string ServiceId,
    string ServiceKind,
    DateTimeOffset CreatedAt,
    string? InitialTitle = null);

public sealed record ChatHistoryTurnDeliveryReservation(
    string DeliveryId,
    string ScopeId,
    string ConversationId,
    string TurnId,
    string UserText,
    string SourceActorId,
    string SourceCommandId,
    string SourceCorrelationId,
    string RequestFingerprint,
    bool CreateConversationIfMissing,
    bool ExposeCreateRecovery = false);

public enum ChatHistoryTurnTerminalStatus
{
    Completed = 1,
    Failed = 2,
    Stopped = 3,
    Blocked = 4,
    OutcomeUncertain = 5,
}

public sealed record ChatHistoryTurnTerminalNotification(
    string DeliveryId,
    string SourceActorId,
    string SourceCommandId,
    ChatHistoryTurnTerminalStatus Status,
    string Text,
    string ErrorCode,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ChatHistoryTurnOperation>? Operations = null);

public enum ChatHistoryTurnOperationKind
{
    Model = 1,
    Tool = 2,
    Other = 3,
}

/// <summary>
/// One Model or Tool operation of a terminal turn, appended with that turn so a
/// reopened transcript can render its trajectory.
/// </summary>
/// <remarks>
/// Content fields are sanitized, size-bounded previews produced by the owning
/// conversation actor. <paramref name="PreviewsTruncated"/> marks them as
/// fragments. Timing is null when the operation never reported it and must not
/// be inferred from arrival time. Tool result bodies are absent by design:
/// untrusted external text must not be retained by the conversation actor.
/// </remarks>
public sealed record ChatHistoryTurnOperation(
    string OperationId,
    int Order,
    ChatHistoryTurnOperationKind Kind,
    string Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Model = null,
    string? Provider = null,
    string? FinishReason = null,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int TotalTokens = 0,
    string? InputPreview = null,
    string? OutputPreview = null,
    string? ArgumentsPreview = null,
    bool PreviewsTruncated = false,
    string? SafeMessage = null,
    IReadOnlyList<string>? AvailableToolNames = null,
    bool ToolCatalogCaptured = false);

public enum ChatHistoryDeleteResultStatus
{
    Accepted = 0,
    NotFound = 1,
}

public sealed record ChatHistoryDeleteResult(ChatHistoryDeleteResultStatus Status)
{
    public static ChatHistoryDeleteResult Accepted() =>
        new(ChatHistoryDeleteResultStatus.Accepted);

    public static ChatHistoryDeleteResult NotFound() =>
        new(ChatHistoryDeleteResultStatus.NotFound);
}
