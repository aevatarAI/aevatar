namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IChatHistoryCommandPort
{
    Task SaveMessagesAsync(
        string scopeId,
        string conversationId,
        ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages,
        CancellationToken ct = default);

    Task DeleteConversationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);
}
