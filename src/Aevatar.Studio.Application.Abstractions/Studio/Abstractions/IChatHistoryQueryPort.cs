namespace Aevatar.Studio.Application.Studio.Abstractions;

// Refactor (iter56/cluster-911-studio-store-query-command):
//   old=Store mixed read/write + hand-built EventEnvelope
//   new=split query/command port + CQRS Core dispatch
public interface IChatHistoryQueryPort
{
    Task<ChatHistoryIndex> GetIndexAsync(ChatHistoryPageRequest request, CancellationToken ct = default);

    Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default) =>
        GetIndexAsync(new ChatHistoryPageRequest(scopeId), ct);

    Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default);

    Task<ChatCreateRecovery?> GetCreateRecoveryAsync(
        ChatCreateRecoveryRequest request,
        CancellationToken ct = default);
}

public sealed record ChatHistoryPageRequest(
    string ScopeId,
    int? Take = null,
    string? Cursor = null);

public sealed record ChatHistoryIndex(
    IReadOnlyList<ConversationMeta> Conversations,
    string? NextCursor = null);

public sealed record ChatCreateRecoveryRequest(
    string ScopeId,
    string CreateIdempotencyKey);

public sealed record ChatCreateRecovery(
    string ConversationId,
    string TurnId,
    string Status,
    long SourceVersion);

public sealed record ConversationMeta(
    string Id,
    string Title,
    string ServiceId,
    string ServiceKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LlmRoute = null,
    string? LlmModel = null);

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
