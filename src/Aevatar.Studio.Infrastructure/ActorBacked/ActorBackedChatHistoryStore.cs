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
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _conversationDocumentReader;
    private readonly IProjectionDocumentReader<ChatCreateRecoveryCurrentStateDocument, string> _createRecoveryDocumentReader;

    public ActorBackedChatHistoryStore(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch,
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> conversationDocumentReader,
        IProjectionDocumentReader<ChatCreateRecoveryCurrentStateDocument, string> createRecoveryDocumentReader)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _conversationDocumentReader = conversationDocumentReader ?? throw new ArgumentNullException(nameof(conversationDocumentReader));
        _createRecoveryDocumentReader = createRecoveryDocumentReader ?? throw new ArgumentNullException(nameof(createRecoveryDocumentReader));
    }

    public async Task<ChatHistoryIndex> GetIndexAsync(ChatHistoryPageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId);
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
                new ProjectionDocumentSort
                {
                    FieldPath = "conversation_id",
                    Direction = ProjectionDocumentSortDirection.Asc,
                },
            ],
            Cursor = NormalizeOptional(request.Cursor),
            Take = NormalizeTake(request.Take),
        }, ct);

        return new ChatHistoryIndex(result.Items
            .Select(ToConversationMeta)
            .ToList()
            .AsReadOnly(),
            string.IsNullOrWhiteSpace(result.NextCursor) ? null : result.NextCursor);
    }

    public async Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId);
        var normalizedConversationId = NormalizeRequired(conversationId);
        var resolved = await GetMatchingConversationDocumentAsync(
                normalizedScopeId,
                normalizedConversationId,
                ct)
            .ConfigureAwait(false);
        var document = resolved.Document;
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
        var normalizedScopeId = NormalizeRequired(scopeId);
        var normalizedConversationId = NormalizeRequired(conversationId);
        var resolved = await GetMatchingConversationDocumentAsync(
                normalizedScopeId,
                normalizedConversationId,
                ct)
            .ConfigureAwait(false);
        if (resolved.Document is null || resolved.Document.Deleted)
            return;

        var actorId = ResolveDocumentActorId(resolved.Document, resolved.DocumentKey);
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        var deleteEvt = new ConversationDeletedEvent
        {
            ConversationId = normalizedConversationId,
            ScopeId = normalizedScopeId,
        };
        var conversationActor = new DispatchOnlyActor(actorId);
        await _commandDispatch.DispatchAsync(conversationActor, deleteEvt, PublisherId, ct);
    }

    public async Task<ChatCreateRecovery?> GetCreateRecoveryAsync(
        ChatCreateRecoveryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId);
        var createIdempotencyKey = NormalizeRequired(request.CreateIdempotencyKey);
        var document = await FindCreateRecoveryDocumentAsync(scopeId, createIdempotencyKey, ct)
            .ConfigureAwait(false);
        return document is null
            ? null
            : new ChatCreateRecovery(
                document.ConversationId,
                document.TurnId,
                document.Status,
                document.SourceVersion);
    }

    // ── Actor resolution ───────────────────────────────────────

    private async Task<IActor> EnsureConversationActorAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(
            ChatHistoryActorIds.Conversation(scopeId, conversationId), ct);
    }

    private async Task<(ChatConversationCurrentStateDocument? Document, string DocumentKey)> GetMatchingConversationDocumentAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct)
    {
        foreach (var documentKey in ConversationDocumentKeys(scopeId, conversationId))
        {
            var document = await _conversationDocumentReader.GetAsync(documentKey, ct).ConfigureAwait(false);
            if (IsMatchingConversationDocument(document, scopeId, conversationId))
                return (document, documentKey);
        }

        return (null, string.Empty);
    }

    private static IEnumerable<string> ConversationDocumentKeys(string scopeId, string conversationId)
    {
        var current = ChatHistoryActorIds.Conversation(scopeId, conversationId);
        yield return current;

        var legacy = ChatHistoryActorIds.LegacyConversation(scopeId, conversationId);
        if (!string.Equals(legacy, current, StringComparison.Ordinal))
            yield return legacy;
    }

    private static bool IsMatchingConversationDocument(
        ChatConversationCurrentStateDocument? document,
        string scopeId,
        string conversationId) =>
        document is not null &&
        string.Equals(document.ScopeId, scopeId, StringComparison.Ordinal) &&
        string.Equals(document.ConversationId, conversationId, StringComparison.Ordinal);

    private async Task<ChatCreateRecoveryCurrentStateDocument?> FindCreateRecoveryDocumentAsync(
        string scopeId,
        string createIdempotencyKey,
        CancellationToken ct)
    {
        var result = await _createRecoveryDocumentReader.QueryAsync(new ProjectionDocumentQuery
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
                    FieldPath = "create_idempotency_key",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(createIdempotencyKey),
                },
            ],
            Take = 1,
        }, ct).ConfigureAwait(false);

        return result.Items.FirstOrDefault(document =>
            string.Equals(document.ScopeId, scopeId, StringComparison.Ordinal) &&
            string.Equals(document.CreateIdempotencyKey, createIdempotencyKey, StringComparison.Ordinal));
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

    private static int NormalizeTake(int? take) =>
        take is > 0
            ? Math.Min(take.Value, MaxPageSize)
            : DefaultPageSize;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string ResolveDocumentActorId(
        ChatConversationCurrentStateDocument document,
        string documentKey)
    {
        if (!string.IsNullOrWhiteSpace(document.ActorId))
            return document.ActorId.Trim();
        if (!string.IsNullOrWhiteSpace(document.Id))
            return document.Id.Trim();
        return documentKey;
    }

    private sealed class DispatchOnlyActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new DispatchOnlyAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class DispatchOnlyAgent : IAgent
    {
        public string Id => "chat-history-dispatch-only";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("chat-history-dispatch-only");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }
}
