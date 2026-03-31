namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IChatHistoryStore
{
    Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default);
    Task SaveIndexAsync(string scopeId, ChatHistoryIndex index, CancellationToken ct = default);
    Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(string scopeId, string conversationId, CancellationToken ct = default);
    Task SaveMessagesAsync(string scopeId, string conversationId, IReadOnlyList<StoredChatMessage> messages, CancellationToken ct = default);
    Task DeleteConversationAsync(string scopeId, string conversationId, CancellationToken ct = default);
}

public sealed record ChatHistoryIndex(IReadOnlyList<ConversationMeta> Conversations);

public sealed record ConversationMeta(
    string Id,
    string Title,
    string ServiceId,
    string ServiceKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record StoredChatMessage(
    string Id,
    string Role,
    string Content,
    long Timestamp,
    string Status,
    string? Error = null,
    string? Thinking = null);
