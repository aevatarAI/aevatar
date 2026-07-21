using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of chat history query and command ports.
/// Reads from the projection document store (CQRS read model).
/// Writes send commands only to <see cref="ChatConversationGAgent"/>
/// through CQRS Core dispatch.
/// </summary>
internal sealed class ActorBackedChatHistoryStore :
    IChatHistoryQueryPort,
    IChatHistoryCommandPort,
    IWorkflowChatHistoryCreateRecoveryReadPort
{
    private const string PublisherId = "aevatar.studio.infrastructure.chat-history";
    private const int DefaultIndexPageSize = 50;
    private const int MaxIndexPageSize = 200;

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioActorCommandDispatch _commandDispatch;
    private readonly IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> _conversationDocumentReader;
    private readonly IProjectionDocumentReader<ChatHistoryCreateRecoveryCurrentStateDocument, string> _createRecoveryDocumentReader;

    public ActorBackedChatHistoryStore(
        IStudioActorBootstrap bootstrap,
        StudioActorCommandDispatch commandDispatch,
        IProjectionDocumentReader<ChatConversationCurrentStateDocument, string> conversationDocumentReader,
        IProjectionDocumentReader<ChatHistoryCreateRecoveryCurrentStateDocument, string> createRecoveryDocumentReader)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _conversationDocumentReader = conversationDocumentReader ?? throw new ArgumentNullException(nameof(conversationDocumentReader));
        _createRecoveryDocumentReader = createRecoveryDocumentReader ?? throw new ArgumentNullException(nameof(createRecoveryDocumentReader));
    }

    public async Task<ChatHistoryIndexPage> GetIndexAsync(
        ChatHistoryIndexPageRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeOptional(request.ScopeId);
        if (scopeId == null)
            return new ChatHistoryIndexPage([], null);

        var pageSize = request.PageSize <= 0
            ? DefaultIndexPageSize
            : Math.Clamp(request.PageSize, 1, MaxIndexPageSize);
        var result = await _conversationDocumentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ChatConversationCurrentStateDocument.ScopeId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(scopeId),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ChatConversationCurrentStateDocument.Deleted),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromBool(false),
                },
            ],
            Sorts =
            [
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(ChatConversationCurrentStateDocument.UpdatedAtMs),
                    Direction = ProjectionDocumentSortDirection.Desc,
                },
                new ProjectionDocumentSort
                {
                    FieldPath = nameof(ChatConversationCurrentStateDocument.ConversationId),
                    Direction = ProjectionDocumentSortDirection.Asc,
                },
            ],
            Cursor = NormalizeOptional(request.Cursor),
            Take = pageSize,
        }, ct);

        return new ChatHistoryIndexPage(
            result.Items
                .Select(ToConversationMeta)
                .ToList()
                .AsReadOnly(),
            string.IsNullOrWhiteSpace(result.NextCursor) ? null : result.NextCursor);
    }

    public async Task<ChatHistoryConversationMessagesResult> GetMessagesAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var resolved = await ResolveConversationDocumentAsync(scopeId, conversationId, ct);
        if (resolved is null)
            return ChatHistoryConversationMessagesResult.NotFound();

        if (resolved.Value.Document.Turns.Count == 0)
            return ChatHistoryConversationMessagesResult.Found([]);

        return ChatHistoryConversationMessagesResult.Found(resolved.Value.Document.Turns
            .OrderBy(static turn => turn.Sequence)
            .SelectMany(ToStoredChatMessages)
            .ToList()
            .AsReadOnly());
    }

    public async Task SaveMessagesAsync(
        string scopeId, string conversationId, ConversationMeta meta,
        IReadOnlyList<StoredChatMessage> messages, CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedConversationId = NormalizeOptional(conversationId);
        if (normalizedScopeId == null || normalizedConversationId == null)
            return;

        var conversationActor = await EnsureConversationActorAsync(normalizedScopeId, normalizedConversationId, ct);
        var turn = ToAppendCommand(normalizedScopeId, normalizedConversationId, meta, messages);
        if (turn is not null)
            await _commandDispatch.DispatchAsync(conversationActor, turn, PublisherId, ct);
    }

    public async Task<ChatHistoryDeleteResult> DeleteConversationAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var resolved = await ResolveConversationDocumentAsync(scopeId, conversationId, ct);
        if (resolved is null)
            return ChatHistoryDeleteResult.NotFound();

        var conversationActor = await EnsureConversationActorAsync(resolved.Value.ActorId, ct);
        var deleteEvt = new ConversationDeletedEvent
        {
            ConversationId = resolved.Value.Document.ConversationId,
            ScopeId = resolved.Value.Document.ScopeId,
        };
        await _commandDispatch.DispatchAsync(conversationActor, deleteEvt, PublisherId, ct);
        return ChatHistoryDeleteResult.Accepted();
    }

    public async Task<ChatHistoryCreateRecoveryResult> GetCreateRecoveryAsync(
        string scopeId,
        string commandId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedCommandId = NormalizeOptional(commandId);
        if (normalizedScopeId == null || normalizedCommandId == null)
            return ChatHistoryCreateRecoveryResult.NotFound(scopeId, commandId);

        var recoveryId = ChatHistoryCreateRecoveryIds.FromScopeAndCommandId(normalizedScopeId, normalizedCommandId);
        var document = await _createRecoveryDocumentReader.GetAsync(recoveryId, ct).ConfigureAwait(false);
        if (document is null ||
            !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(document.WorkflowCommandId, normalizedCommandId, StringComparison.Ordinal))
        {
            return ChatHistoryCreateRecoveryResult.NotFound(normalizedScopeId, normalizedCommandId);
        }

        return new ChatHistoryCreateRecoveryResult(
            ToCreateRecoveryStatus(document.Status),
            document.ScopeId,
            normalizedCommandId,
            EmptyToNull(document.ConversationId),
            EmptyToNull(document.TurnId),
            EmptyToNull(document.WorkflowActorId),
            EmptyToNull(document.WorkflowCommandId),
            EmptyToNull(document.WorkflowCorrelationId),
            EmptyToNull(document.RequestFingerprint),
            document.StateVersion,
            document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch);
    }

    public async Task<WorkflowChatHistoryCreateRecovery?> GetAsync(
        string scopeId,
        string commandId,
        CancellationToken ct = default)
    {
        var result = await GetCreateRecoveryAsync(scopeId, commandId, ct).ConfigureAwait(false);
        if (result.Status == ChatHistoryCreateRecoveryStatus.NotFound)
            return null;

        return new WorkflowChatHistoryCreateRecovery(
            ToWorkflowCreateRecoveryStatus(result.Status),
            result.ScopeId,
            result.CommandId,
            result.ConversationId,
            result.TurnId,
            result.WorkflowActorId,
            result.WorkflowCommandId,
            result.WorkflowCorrelationId,
            result.RequestFingerprint,
            result.StateVersion,
            result.UpdatedAt);
    }

    // ── Actor resolution ───────────────────────────────────────

    private async Task<IActor> EnsureConversationActorAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(
            ChatHistoryActorIds.Conversation(scopeId, conversationId), ct);
    }

    private async Task<IActor> EnsureConversationActorAsync(
        string actorId,
        CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(actorId, ct);
    }

    private async Task<ResolvedConversationDocument?> ResolveConversationDocumentAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedConversationId = NormalizeOptional(conversationId);
        if (normalizedScopeId == null || normalizedConversationId == null)
            return null;

        var actorIds = new[]
        {
            ChatHistoryActorIds.Conversation(normalizedScopeId, normalizedConversationId),
            ChatHistoryActorIds.LegacyConversation(normalizedScopeId, normalizedConversationId),
        };

        foreach (var actorId in actorIds)
        {
            var document = await _conversationDocumentReader.GetAsync(actorId, ct).ConfigureAwait(false);
            if (document is null ||
                document.Deleted ||
                !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
                !string.Equals(document.ConversationId, normalizedConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            var dispatchActorId = string.IsNullOrWhiteSpace(document.ActorId)
                ? actorId
                : document.ActorId.Trim();
            return new ResolvedConversationDocument(dispatchActorId, document);
        }

        return null;
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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ChatHistoryCreateRecoveryStatus ToCreateRecoveryStatus(string status) =>
        status switch
        {
            "reserved" => ChatHistoryCreateRecoveryStatus.Reserved,
            "bound" => ChatHistoryCreateRecoveryStatus.Bound,
            "append_dispatched" => ChatHistoryCreateRecoveryStatus.AppendDispatched,
            "abandoned" => ChatHistoryCreateRecoveryStatus.Abandoned,
            "failed" => ChatHistoryCreateRecoveryStatus.Failed,
            "append_committed" => ChatHistoryCreateRecoveryStatus.AppendCommitted,
            "append_rejected" => ChatHistoryCreateRecoveryStatus.AppendRejected,
            _ => ChatHistoryCreateRecoveryStatus.NotFound,
        };

    private static WorkflowChatHistoryCreateRecoveryStatus ToWorkflowCreateRecoveryStatus(
        ChatHistoryCreateRecoveryStatus status) =>
        status switch
        {
            ChatHistoryCreateRecoveryStatus.Reserved => WorkflowChatHistoryCreateRecoveryStatus.Reserved,
            ChatHistoryCreateRecoveryStatus.Bound => WorkflowChatHistoryCreateRecoveryStatus.Bound,
            ChatHistoryCreateRecoveryStatus.AppendDispatched => WorkflowChatHistoryCreateRecoveryStatus.AppendDispatched,
            ChatHistoryCreateRecoveryStatus.Abandoned => WorkflowChatHistoryCreateRecoveryStatus.Abandoned,
            ChatHistoryCreateRecoveryStatus.Failed => WorkflowChatHistoryCreateRecoveryStatus.Failed,
            ChatHistoryCreateRecoveryStatus.AppendCommitted => WorkflowChatHistoryCreateRecoveryStatus.AppendCommitted,
            ChatHistoryCreateRecoveryStatus.AppendRejected => WorkflowChatHistoryCreateRecoveryStatus.AppendRejected,
            _ => WorkflowChatHistoryCreateRecoveryStatus.NotFound,
        };

    private readonly record struct ResolvedConversationDocument(
        string ActorId,
        ChatConversationCurrentStateDocument Document);
}
