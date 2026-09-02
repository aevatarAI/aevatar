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
    DateTimeOffset ObservedAt);

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
