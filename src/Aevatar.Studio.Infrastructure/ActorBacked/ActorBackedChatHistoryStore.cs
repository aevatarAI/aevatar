using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of chat history query and command ports.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands only to <see cref="ChatConversationGAgent"/>
/// through CQRS Core dispatch.
/// </summary>
internal sealed class ActorBackedChatHistoryStore : IChatHistoryQueryPort, IChatHistoryCommandPort
{
    private const string PublisherId = "aevatar.studio.infrastructure.chat-history";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _conversationDocumentReader;

    public ActorBackedChatHistoryStore(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch,
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> conversationDocumentReader)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _conversationDocumentReader = conversationDocumentReader ?? throw new ArgumentNullException(nameof(conversationDocumentReader));
    }

    public async Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
    {
        var result = await _conversationDocumentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = "scope_id",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(scopeId),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = "deleted",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromBool(false),
                },
            ],
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = "updated_at_ms",
                    Direction = ProjectionDocumentSortDirection.Desc,
                },
            ],
            Take = ChatConversationGAgent.MaxTurns,
        }, ct);

        return new ChatHistoryIndex(result.Items
            .Select(ToConversationMeta)
            .ToList()
            .AsReadOnly());
    }

    public async Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var actorId = ChatHistoryActorIds.Conversation(scopeId, conversationId);
        var document = await _conversationDocumentReader.GetAsync(actorId, ct);
        if (document is null || document.Deleted)
            return [];

        if (document.Turns.Count == 0)
            return [];

        return document.Turns
            .OrderBy(static turn => turn.Sequence)
            .SelectMany(ToStoredChatMessages)
            .ToList()
            .AsReadOnly();
    }

    public async Task SaveMessagesAsync(
        string scopeId, string conversationId, ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages, CancellationToken ct = default)
    {
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var turn = ToAppendCommand(scopeId, conversationId, meta, messages);
        if (turn is not null)
            await _commandDispatch.DispatchAsync(conversationActor, turn, PublisherId, ct);
    }

    public async Task DeleteConversationAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var deleteEvt = new ConversationDeletedEvent
        {
            ConversationId = conversationId,
            ScopeId = scopeId,
        };
        await _commandDispatch.DispatchAsync(conversationActor, deleteEvt, PublisherId, ct);
    }

    // ── Actor resolution ───────────────────────────────────────

    private async Task<IActor> EnsureConversationActorAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(
            ChatHistoryActorIds.Conversation(scopeId, conversationId), ct);
    }

    // ── Mapping helpers ────────────────────────────────────────

    private static ConversationMeta ToConversationMeta(ChatConversationCurrentStateDocument document) =>
        new(
            Id: document.ConversationId,
            Title: document.Title,
            ServiceId: document.ServiceId,
            ServiceKind: document.ServiceKind,
            CreatedAt: FromUnixMs(document.CreatedAtMs),
            UpdatedAt: FromUnixMs(document.UpdatedAtMs),
            MessageCount: document.MessageCount,
            LlmRoute: string.IsNullOrEmpty(document.LlmRoute) ? null : document.LlmRoute,
            LlmModel: string.IsNullOrEmpty(document.LlmModel) ? null : document.LlmModel);

    private static IEnumerable<StoredChatMessage> ToStoredChatMessages(ChatConversationTurnDocument turn)
    {
        yield return new StoredChatMessage(
            Id: $"{turn.TurnId}:user",
            Role: "user",
            Content: turn.UserText,
            Timestamp: turn.TerminalTimeMs,
            Status: "complete",
            TurnId: turn.TurnId);

        yield return new StoredChatMessage(
            Id: $"{turn.TurnId}:assistant",
            Role: "assistant",
            Content: turn.AssistantText,
            Timestamp: turn.TerminalTimeMs,
            Status: turn.TerminalStatus switch
            {
                "error" => "error",
                "blocked" => "blocked",
                _ => "complete",
            },
            Error: string.IsNullOrEmpty(turn.SanitizedError) ? null : turn.SanitizedError,
            TurnId: turn.TurnId);
    }

    private static AppendChatTurnCommand? ToAppendCommand(
        string scopeId,
        string conversationId,
        ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages)
    {
        var assistant = messages.LastOrDefault(static message => message.Role == "assistant");
        if (assistant is null)
            return null;

        var user = messages.LastOrDefault(message =>
            message.Role == "user" && message.Timestamp <= assistant.Timestamp);
        if (user is null)
            return null;

        return new AppendChatTurnCommand
        {
            ScopeId = scopeId,
            ConversationId = conversationId,
            Title = meta.Title ?? string.Empty,
            ServiceId = meta.ServiceId ?? string.Empty,
            ServiceKind = meta.ServiceKind ?? string.Empty,
            Turn = new ChatTurn
            {
                TurnId = ResolveTurnId(user, assistant),
                UserText = user.Content ?? string.Empty,
                AssistantText = assistant.Content ?? string.Empty,
                TerminalStatus = assistant.Status switch
                {
                    "error" => ChatTurnTerminalStatus.Failed,
                    "blocked" => ChatTurnTerminalStatus.Blocked,
                    _ => ChatTurnTerminalStatus.Completed,
                },
                SanitizedError = assistant.Error ?? string.Empty,
                TerminalTime = Timestamp.FromDateTimeOffset(FromUnixMs(assistant.Timestamp)),
                LlmRoute = meta.LlmRoute ?? string.Empty,
                LlmModel = meta.LlmModel ?? string.Empty,
            },
        };
    }

    private static string ResolveTurnId(StoredChatMessage user, StoredChatMessage assistant)
    {
        if (!string.IsNullOrWhiteSpace(assistant.TurnId))
            return assistant.TurnId.Trim();
        if (!string.IsNullOrWhiteSpace(user.TurnId))
            return user.TurnId.Trim();
        return assistant.Id ?? user.Id ?? Guid.NewGuid().ToString("N");
    }

    private static DateTimeOffset FromUnixMs(long ms) =>
        ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UnixEpoch;
}
