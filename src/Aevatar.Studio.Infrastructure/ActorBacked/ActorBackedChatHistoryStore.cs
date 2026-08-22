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
/// Writes send commands to the authoritative conversation and turn-delivery
/// actors through CQRS Core dispatch.
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
        var lookup = await LookupConversationDocumentAsync(scopeId, conversationId, ct);
        if (lookup.Resolved is null)
        {
            if (lookup.Deleted)
                return ChatHistoryConversationMessagesResult.NotFound();

            return await HasPendingCreateReservationAsync(scopeId, conversationId, ct)
                .ConfigureAwait(false)
                ? ChatHistoryConversationMessagesResult.Pending()
                : ChatHistoryConversationMessagesResult.NotFound();
        }

        var resolved = lookup.Resolved.Value;

        if (resolved.Document.Turns.Count == 0)
            return ChatHistoryConversationMessagesResult.Found([], resolved.Document.StateVersion);

        var orderedTurns = resolved.Document.Turns
            .OrderBy(static turn => turn.Sequence)
            .ToList();
        return ChatHistoryConversationMessagesResult.Found(
            orderedTurns
                .SelectMany(ToStoredChatMessages)
                .ToList()
                .AsReadOnly(),
            resolved.Document.StateVersion,
            orderedTurns
                .SelectMany(ToStoredTurnOperations)
                .ToList()
                .AsReadOnly());
    }

    private static IEnumerable<StoredChatTurnOperation> ToStoredTurnOperations(
        ChatConversationTurnDocument turn) =>
        turn.Operations
            .OrderBy(static operation => operation.Order)
            .Select(operation => new StoredChatTurnOperation(
                TurnId: turn.TurnId,
                OperationId: operation.OperationId,
                Order: operation.Order,
                Kind: operation.Kind,
                Title: operation.Title,
                Status: operation.Status,
                StartedAt: ToInstant(operation.StartedAtMs),
                CompletedAt: ToInstant(operation.CompletedAtMs),
                Model: NullIfEmpty(operation.Model),
                Provider: NullIfEmpty(operation.Provider),
                FinishReason: NullIfEmpty(operation.FinishReason),
                PromptTokens: NullIfZero(operation.PromptTokens),
                CompletionTokens: NullIfZero(operation.CompletionTokens),
                TotalTokens: NullIfZero(operation.TotalTokens),
                InputPreview: NullIfEmpty(operation.InputPreview),
                OutputPreview: NullIfEmpty(operation.OutputPreview),
                ArgumentsPreview: NullIfEmpty(operation.ArgumentsPreview),
                PreviewsTruncated: operation.PreviewsTruncated,
                SafeMessage: NullIfEmpty(operation.SafeMessage),
                AvailableToolNames: operation.AvailableToolNames.ToList().AsReadOnly(),
                ToolCatalogCaptured: operation.ToolCatalogCaptured));

    private static DateTimeOffset? ToInstant(long unixMs) =>
        unixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs) : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static int? NullIfZero(int value) => value > 0 ? value : null;

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

    public async Task InitializeConversationAsync(
        ChatHistoryConversationInitialization request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = NormalizeRequired(request.OperationId, nameof(request.OperationId));
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var conversationId = NormalizeRequired(request.ConversationId, nameof(request.ConversationId));
        var serviceId = NormalizeRequired(request.ServiceId, nameof(request.ServiceId));
        var serviceKind = NormalizeRequired(request.ServiceKind, nameof(request.ServiceKind));
        var conversationActor = await EnsureConversationActorAsync(scopeId, conversationId, ct);
        var command = new InitializeChatConversationCommand
        {
            OperationId = operationId,
            ScopeId = scopeId,
            ConversationId = conversationId,
            ServiceId = serviceId,
            ServiceKind = serviceKind,
            CreatedAt = Timestamp.FromDateTimeOffset(request.CreatedAt),
            InitialTitle = NormalizeOptional(request.InitialTitle) ?? string.Empty,
        };
        await _commandDispatch.DispatchAsync(conversationActor, command, PublisherId, ct);
    }

    public async Task ReserveTurnDeliveryAsync(
        ChatHistoryTurnDeliveryReservation request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deliveryId = NormalizeRequired(request.DeliveryId, nameof(request.DeliveryId));
        var deliveryActor = await EnsureDeliveryActorAsync(deliveryId, ct);
        var command = new ChatTurnHistoryDeliveryReserveRequested
        {
            DeliveryId = deliveryId,
            ScopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId)),
            ConversationId = NormalizeRequired(request.ConversationId, nameof(request.ConversationId)),
            TurnId = NormalizeRequired(request.TurnId, nameof(request.TurnId)),
            UserText = NormalizeRequired(request.UserText, nameof(request.UserText)),
            SourceActorId = NormalizeRequired(request.SourceActorId, nameof(request.SourceActorId)),
            SourceCommandId = NormalizeRequired(request.SourceCommandId, nameof(request.SourceCommandId)),
            SourceCorrelationId = NormalizeOptional(request.SourceCorrelationId) ?? string.Empty,
            RequestFingerprint = NormalizeOptional(request.RequestFingerprint) ?? string.Empty,
            CreateConversationIfMissing = request.CreateConversationIfMissing,
            ExposeCreateRecovery = request.ExposeCreateRecovery,
        };
        await _commandDispatch.DispatchAsync(deliveryActor, command, PublisherId, ct);
    }

    public async Task NotifyTurnTerminalAsync(
        ChatHistoryTurnTerminalNotification notification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var deliveryId = NormalizeRequired(notification.DeliveryId, nameof(notification.DeliveryId));
        var sourceActorId = NormalizeRequired(notification.SourceActorId, nameof(notification.SourceActorId));
        var deliveryActor = await EnsureDeliveryActorAsync(deliveryId, ct);
        var command = new ChatTurnHistorySourceTerminalNotified
        {
            DeliveryId = deliveryId,
            SourceActorId = sourceActorId,
            SourceCommandId = NormalizeRequired(notification.SourceCommandId, nameof(notification.SourceCommandId)),
            Status = ToTerminalStatus(notification.Status),
            Text = NormalizeOptional(notification.Text) ?? string.Empty,
            ErrorCode = NormalizeOptional(notification.ErrorCode) ?? string.Empty,
            ObservedAtUnixMs = notification.ObservedAt.ToUnixTimeMilliseconds(),
        };
        if (notification.Operations is { Count: > 0 } operations)
            command.Operations.AddRange(operations.Select(ToChatTurnOperation));
        await _commandDispatch.DispatchAsync(deliveryActor, command, sourceActorId, ct);
    }

    private static ChatTurnOperation ToChatTurnOperation(ChatHistoryTurnOperation operation)
    {
        var mapped = new ChatTurnOperation
        {
            OperationId = operation.OperationId,
            Order = operation.Order,
            Kind = operation.Kind switch
            {
                ChatHistoryTurnOperationKind.Model => ChatTurnOperationKind.Model,
                ChatHistoryTurnOperationKind.Tool => ChatTurnOperationKind.Tool,
                _ => ChatTurnOperationKind.Other,
            },
            Title = operation.Title,
            Status = operation.Status,
            Model = operation.Model ?? string.Empty,
            Provider = operation.Provider ?? string.Empty,
            FinishReason = operation.FinishReason ?? string.Empty,
            PromptTokens = operation.PromptTokens,
            CompletionTokens = operation.CompletionTokens,
            TotalTokens = operation.TotalTokens,
            InputPreview = operation.InputPreview ?? string.Empty,
            OutputPreview = operation.OutputPreview ?? string.Empty,
            ArgumentsPreview = operation.ArgumentsPreview ?? string.Empty,
            PreviewsTruncated = operation.PreviewsTruncated,
            SafeMessage = operation.SafeMessage ?? string.Empty,
        };
        if (operation.StartedAt is { } startedAt)
            mapped.StartedAt = Timestamp.FromDateTimeOffset(startedAt);
        if (operation.CompletedAt is { } completedAt)
            mapped.CompletedAt = Timestamp.FromDateTimeOffset(completedAt);
        if (operation.AvailableToolNames is { Count: > 0 } availableToolNames)
            mapped.AvailableToolNames.AddRange(availableToolNames);
        mapped.ToolCatalogCaptured = operation.ToolCatalogCaptured;
        return mapped;
    }

    public async Task<ChatHistoryDeleteResult> DeleteConversationAsync(
        string scopeId, string conversationId, CancellationToken ct = default)
    {
        var lookup = await LookupConversationDocumentAsync(scopeId, conversationId, ct);
        if (lookup.Resolved is null)
            return ChatHistoryDeleteResult.NotFound();

        var resolved = lookup.Resolved.Value;
        var conversationActor = await EnsureConversationActorAsync(resolved.ActorId, ct);
        var command = new DeleteConversationCommand
        {
            ConversationId = resolved.Document.ConversationId,
            ScopeId = resolved.Document.ScopeId,
        };
        await _commandDispatch.DispatchAsync(conversationActor, command, PublisherId, ct);
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

    public async Task<WorkflowChatHistoryCreateRecovery?> GetByConversationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct = default)
    {
        var document = await FindAcknowledgedCreateReservationAsync(scopeId, conversationId, ct)
            .ConfigureAwait(false);
        if (document is null)
            return null;

        return new WorkflowChatHistoryCreateRecovery(
            ToWorkflowCreateRecoveryStatus(ToCreateRecoveryStatus(document.Status)),
            document.ScopeId,
            document.WorkflowCommandId,
            EmptyToNull(document.ConversationId),
            EmptyToNull(document.TurnId),
            EmptyToNull(document.WorkflowActorId),
            EmptyToNull(document.WorkflowCommandId),
            EmptyToNull(document.WorkflowCorrelationId),
            EmptyToNull(document.RequestFingerprint),
            document.StateVersion,
            document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch);
    }

    // ── Actor resolution ───────────────────────────────────────

    private async Task<IActor> EnsureConversationActorAsync(
        string scopeId, string conversationId, CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(
            ChatHistoryActorIds.Conversation(scopeId, conversationId), ct);
    }

    private async Task<IActor> EnsureDeliveryActorAsync(string deliveryId, CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatTurnHistoryDeliveryGAgent>(
            ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId), ct);
    }

    private async Task<IActor> EnsureConversationActorAsync(
        string actorId,
        CancellationToken ct)
    {
        return await _bootstrap.EnsureAsync<ChatConversationGAgent>(actorId, ct);
    }

    private async Task<ConversationDocumentLookup> LookupConversationDocumentAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedConversationId = NormalizeOptional(conversationId);
        if (normalizedScopeId == null || normalizedConversationId == null)
            return ConversationDocumentLookup.Missing;

        var actorIds = new[]
        {
            ChatHistoryActorIds.Conversation(normalizedScopeId, normalizedConversationId),
            ChatHistoryActorIds.LegacyConversation(normalizedScopeId, normalizedConversationId),
        };

        foreach (var actorId in actorIds)
        {
            var document = await _conversationDocumentReader.GetAsync(actorId, ct).ConfigureAwait(false);
            if (document is null ||
                !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
                !string.Equals(document.ConversationId, normalizedConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (document.Deleted)
                return ConversationDocumentLookup.DeletedConversation;

            var dispatchActorId = string.IsNullOrWhiteSpace(document.ActorId)
                ? actorId
                : document.ActorId.Trim();
            return ConversationDocumentLookup.Found(
                new ResolvedConversationDocument(dispatchActorId, document));
        }

        return ConversationDocumentLookup.Missing;
    }

    private async Task<bool> HasPendingCreateReservationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct) =>
        await FindAcknowledgedCreateReservationAsync(scopeId, conversationId, ct)
            .ConfigureAwait(false) is not null;

    private async Task<ChatHistoryCreateRecoveryCurrentStateDocument?> FindAcknowledgedCreateReservationAsync(
        string scopeId,
        string conversationId,
        CancellationToken ct)
    {
        var normalizedScopeId = NormalizeOptional(scopeId);
        var normalizedConversationId = NormalizeOptional(conversationId);
        if (normalizedScopeId == null || normalizedConversationId == null)
            return null;

        var reservations = await _createRecoveryDocumentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ChatHistoryCreateRecoveryCurrentStateDocument.ScopeId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedScopeId),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ChatHistoryCreateRecoveryCurrentStateDocument.ConversationId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedConversationId),
                },
            ],
            Take = 1,
        }, ct).ConfigureAwait(false);

        return reservations.Items.FirstOrDefault(reservation =>
            reservation.StateVersion > 0 &&
            string.Equals(reservation.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
            string.Equals(reservation.ConversationId, normalizedConversationId, StringComparison.Ordinal) &&
            ToCreateRecoveryStatus(reservation.Status) is not (
                ChatHistoryCreateRecoveryStatus.NotFound or
                ChatHistoryCreateRecoveryStatus.Abandoned));
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
            LlmModel: string.IsNullOrEmpty(document.LlmModel) ? null : document.LlmModel,
            StateVersion: document.StateVersion);

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
                "outcome_uncertain" => "outcome_uncertain",
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
                    "outcome_uncertain" => ChatTurnTerminalStatus.OutcomeUncertain,
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

    private static string NormalizeRequired(string? value, string parameterName) =>
        NormalizeOptional(value) ?? throw new ArgumentException("Value is required.", parameterName);

    private static ChatTurnTerminalStatus ToTerminalStatus(ChatHistoryTurnTerminalStatus status) =>
        status switch
        {
            ChatHistoryTurnTerminalStatus.Completed => ChatTurnTerminalStatus.Completed,
            ChatHistoryTurnTerminalStatus.Failed => ChatTurnTerminalStatus.Failed,
            ChatHistoryTurnTerminalStatus.Stopped => ChatTurnTerminalStatus.Stopped,
            ChatHistoryTurnTerminalStatus.Blocked => ChatTurnTerminalStatus.Blocked,
            ChatHistoryTurnTerminalStatus.OutcomeUncertain => ChatTurnTerminalStatus.OutcomeUncertain,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Terminal status must be closed."),
        };

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
            "terminal_reconciliation_prepared" => ChatHistoryCreateRecoveryStatus.TerminalReconciliationPrepared,
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
            ChatHistoryCreateRecoveryStatus.TerminalReconciliationPrepared =>
                WorkflowChatHistoryCreateRecoveryStatus.TerminalReconciliationPrepared,
            _ => WorkflowChatHistoryCreateRecoveryStatus.NotFound,
        };

    private readonly record struct ResolvedConversationDocument(
        string ActorId,
        ChatConversationCurrentStateDocument Document);

    private readonly record struct ConversationDocumentLookup(
        ResolvedConversationDocument? Resolved,
        bool Deleted)
    {
        public static ConversationDocumentLookup Missing { get; } = new(null, false);

        public static ConversationDocumentLookup DeletedConversation { get; } = new(null, true);

        public static ConversationDocumentLookup Found(ResolvedConversationDocument resolved) =>
            new(resolved, false);
    }
}
