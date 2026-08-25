using System.Diagnostics;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChatHistory;

[GAgent("chat.history.turn-delivery")]
public sealed class ChatTurnHistoryDeliveryGAgent : GAgentBase<ChatTurnHistoryDeliveryState>,
    IProjectedActor
{
    public static string ProjectionKind => "chat-history-turn-delivery";

    private const string ConversationAppendPublisherId = "chat-history-turn-delivery";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<ChatTurnHistoryDeliveryGAgent> _logger;
    private readonly TimeProvider _timeProvider;

    public ChatTurnHistoryDeliveryGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<ChatTurnHistoryDeliveryGAgent> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override ChatTurnHistoryDeliveryState TransitionState(
        ChatTurnHistoryDeliveryState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ChatTurnHistoryDeliveryReservedEvent>(ApplyReserved)
            .On<ChatTurnHistoryDeliveryBoundEvent>(ApplyBound)
            .On<ChatTurnHistoryDeliveryTerminalFrameObserved>(ApplyTerminalFrameObserved)
            .On<ChatTurnHistoryDeliveryTerminalReconciledEvent>(ApplyTerminalReconciled)
            .On<ChatTurnHistoryDeliveryAppendDispatchedEvent>(ApplyAppendDispatched)
            .On<ChatTurnHistoryDeliveryAppendResultRecordedEvent>(ApplyAppendResultRecorded)
            .On<ChatTurnHistoryDeliveryAbandonedEvent>(ApplyAbandoned)
            .On<ChatTurnHistoryDeliveryFailedEvent>(ApplyFailed)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await DispatchPendingTerminalAppendAsync(ct);
    }

    [EventHandler]
    public async Task HandleReserveAsync(ChatTurnHistoryDeliveryReserveRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Status != ChatTurnHistoryDeliveryStatus.Unspecified)
        {
            if (HasSameReservation(State, command))
                return;

            throw new InvalidOperationException("Chat history delivery reservation conflicts with the committed reservation.");
        }

        var validation = ValidateReserve(command);
        if (validation is not null)
        {
            await PersistFailureAsync(
                    command.DeliveryId ?? string.Empty,
                    command.SourceActorId ?? string.Empty,
                    command.SourceCommandId ?? string.Empty,
                    validation.Value.Code,
                    validation.Value.Summary);
            return;
        }

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryReservedEvent
        {
            DeliveryId = command.DeliveryId.Trim(),
            ScopeId = command.ScopeId.Trim(),
            ConversationId = command.ConversationId.Trim(),
            TurnId = command.TurnId.Trim(),
            UserText = command.UserText.Trim(),
            SourceActorId = command.SourceActorId.Trim(),
            SourceCommandId = command.SourceCommandId.Trim(),
            SourceCorrelationId = command.SourceCorrelationId?.Trim() ?? string.Empty,
            ReservedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            CreateConversationIfMissing = command.CreateConversationIfMissing,
            RequestFingerprint = command.RequestFingerprint?.Trim() ?? string.Empty,
            ExposeCreateRecovery = command.ExposeCreateRecovery,
        });
    }

    [EventHandler]
    public async Task HandleAcceptedBoundAsync(ChatTurnHistoryDeliveryAcceptedBound command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrentSource(command.DeliveryId, command.SourceActorId, command.SourceCommandId))
            return;
        if (State.Status != ChatTurnHistoryDeliveryStatus.Reserved)
            return;

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryBoundEvent
        {
            DeliveryId = State.DeliveryId,
            SourceActorId = State.SourceActorId,
            SourceCommandId = State.SourceCommandId,
            SourceCorrelationId = string.IsNullOrWhiteSpace(command.SourceCorrelationId)
                ? State.SourceCorrelationId
                : command.SourceCorrelationId.Trim(),
            BoundAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await DispatchPendingTerminalAppendAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleTerminalNotificationAsync(WorkflowRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
        _logger.LogWarning(
            "Chat turn history delivery workflow terminal notification received: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} sourceActorId={SourceActorId} publisherActorId={PublisherActorId} sourceCommandId={SourceCommandId} status={TerminalStatus} currentStatus={CurrentStatus} terminalStatus={CurrentTerminalStatus}",
            Id,
            notification.DeliveryId,
            notification.WorkflowActorId,
            publisherActorId,
            notification.WorkflowCommandId,
            notification.Status,
            State.Status,
            State.TerminalStatus);
        if (!IsValidNotificationEnvelope(notification, publisherActorId) ||
            !IsCurrentSource(notification.DeliveryId, notification.WorkflowActorId, notification.WorkflowCommandId))
        {
            return;
        }

        await HandleSourceTerminalCoreAsync(new ChatTurnHistorySourceTerminalNotified
        {
            DeliveryId = notification.DeliveryId.Trim(),
            SourceActorId = notification.WorkflowActorId.Trim(),
            SourceCommandId = notification.WorkflowCommandId.Trim(),
            Status = ToChatTurnTerminalStatus(notification.Status),
            Text = ResolveTerminalText(notification),
            ErrorCode = ResolveTerminalErrorCode(notification),
            ObservedAtUnixMs = ResolveTerminalObservedAt(notification),
        });
    }

    [EventHandler]
    public async Task HandleSourceTerminalAsync(ChatTurnHistorySourceTerminalNotified notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
        if (!IsValidSourceTerminal(notification, publisherActorId) ||
            !IsCurrentSource(notification.DeliveryId, notification.SourceActorId, notification.SourceCommandId))
        {
            return;
        }

        await HandleSourceTerminalCoreAsync(notification);
    }

    private async Task HandleSourceTerminalCoreAsync(ChatTurnHistorySourceTerminalNotified notification)
    {
        var terminal = new ChatTurnHistoryDeliveryTerminalFrameObserved
        {
            DeliveryId = notification.DeliveryId.Trim(),
            SourceActorId = notification.SourceActorId.Trim(),
            SourceCommandId = notification.SourceCommandId.Trim(),
            Status = notification.Status,
            Text = notification.Text?.Trim() ?? string.Empty,
            ErrorCode = notification.ErrorCode?.Trim() ?? string.Empty,
            ObservedAtUnixMs = notification.ObservedAtUnixMs,
            Operations = { notification.Operations.Select(operation => operation.Clone()) },
        };
        if (State.TerminalStatus != ChatTurnTerminalStatus.Unspecified)
        {
            if (HasSameTerminalFrame(State, terminal))
            {
                if (State.Status is ChatTurnHistoryDeliveryStatus.Reserved or ChatTurnHistoryDeliveryStatus.Bound)
                    await DispatchPendingTerminalAppendAsync(CancellationToken.None);
                return;
            }

            if (State.Status != ChatTurnHistoryDeliveryStatus.AppendCommitted ||
                !CanReconcileTerminal(State.TerminalStatus, terminal.Status))
                throw new InvalidOperationException("Chat history delivery terminal conflicts with the committed terminal.");

            await PersistDomainEventAsync(new ChatTurnHistoryDeliveryTerminalReconciledEvent
            {
                DeliveryId = terminal.DeliveryId,
                SourceActorId = terminal.SourceActorId,
                SourceCommandId = terminal.SourceCommandId,
                PreviousStatus = State.TerminalStatus,
                Status = terminal.Status,
                Text = terminal.Text,
                ErrorCode = terminal.ErrorCode,
                ObservedAtUnixMs = terminal.ObservedAtUnixMs,
                Operations = { terminal.Operations.Select(operation => operation.Clone()) },
            });
            await DispatchPendingTerminalAppendAsync(CancellationToken.None);
            return;
        }

        if (State.Status is not (ChatTurnHistoryDeliveryStatus.Reserved or ChatTurnHistoryDeliveryStatus.Bound))
            return;

        var terminalFramePersistStarted = Stopwatch.GetTimestamp();
        await PersistDomainEventAsync(terminal);
        _logger.LogWarning(
            "Chat turn history delivery terminal frame persisted: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} sourceActorId={SourceActorId} sourceCommandId={SourceCommandId} status={TerminalStatus} currentStatus={CurrentStatus} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.SourceActorId,
            State.SourceCommandId,
            State.TerminalStatus,
            State.Status,
            Stopwatch.GetElapsedTime(terminalFramePersistStarted).TotalMilliseconds);
        await DispatchPendingTerminalAppendAsync(CancellationToken.None);
    }

    private async Task DispatchPendingTerminalAppendAsync(CancellationToken ct)
    {
        if (State.Status is not (ChatTurnHistoryDeliveryStatus.Reserved or
                                ChatTurnHistoryDeliveryStatus.Bound or
                                ChatTurnHistoryDeliveryStatus.TerminalReconciliationPrepared) ||
            State.TerminalStatus == ChatTurnTerminalStatus.Unspecified)
        {
            return;
        }

        var appendCommand = BuildAppendCommandFromState();
        var conversationActorId = ChatHistoryActorIds.Conversation(State.ScopeId, State.ConversationId);
        var appendAttempt = Math.Max(1, State.AppendAttempt + 1);
        _logger.LogWarning(
            "Chat turn history delivery append dispatch starting: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} conversationActorId={ConversationActorId} sourceActorId={SourceActorId} sourceCommandId={SourceCommandId} appendAttempt={AppendAttempt} currentStatus={CurrentStatus} terminalStatus={TerminalStatus}",
            Id,
            State.DeliveryId,
            conversationActorId,
            State.SourceActorId,
            State.SourceCommandId,
            appendAttempt,
            State.Status,
            State.TerminalStatus);
        var existsStarted = Stopwatch.GetTimestamp();
        var conversationExists = await _actorRuntime.ExistsAsync(conversationActorId);
        _logger.LogWarning(
            "Chat turn history delivery conversation exists check completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} conversationActorId={ConversationActorId} sourceCommandId={SourceCommandId} exists={Exists} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            conversationActorId,
            State.SourceCommandId,
            conversationExists,
            Stopwatch.GetElapsedTime(existsStarted).TotalMilliseconds);
        if (!conversationExists)
        {
            if (!State.CreateConversationIfMissing)
            {
                await PersistFailureAsync(
                        State.DeliveryId,
                        State.SourceActorId,
                        State.SourceCommandId,
                        "conversation_not_found",
                        "Chat history conversation was not found.");
                return;
            }

            var createStarted = Stopwatch.GetTimestamp();
            await _actorRuntime.CreateAsync<ChatConversationGAgent>(conversationActorId, ct);
            _logger.LogWarning(
                "Chat turn history delivery conversation create completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} conversationActorId={ConversationActorId} sourceCommandId={SourceCommandId} elapsedMs={ElapsedMs}",
                Id,
                State.DeliveryId,
                conversationActorId,
                State.SourceCommandId,
                Stopwatch.GetElapsedTime(createStarted).TotalMilliseconds);
        }

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(appendCommand),
            Route = EnvelopeRouteSemantics.CreateDirect(ConversationAppendPublisherId, conversationActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(State.SourceCorrelationId)
                    ? State.SourceCommandId
                    : State.SourceCorrelationId,
            },
        };
        envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId =
            $"chat-history-append:{State.DeliveryId}:{appendAttempt}";

        var dispatchStarted = Stopwatch.GetTimestamp();
        var admission = await _dispatchPort.DispatchAsync(conversationActorId, envelope, ct);
        _logger.LogWarning(
            "Chat turn history delivery append dispatch completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} conversationActorId={ConversationActorId} sourceCommandId={SourceCommandId} appendAttempt={AppendAttempt} accepted={Accepted} commandId={CommandId} correlationId={CorrelationId} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            conversationActorId,
            State.SourceCommandId,
            appendAttempt,
            admission.Accepted,
            admission.CommandId,
            admission.CorrelationId,
            Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds);
        if (!admission.Accepted)
        {
            await PersistFailureAsync(
                    State.DeliveryId,
                    State.SourceActorId,
                    State.SourceCommandId,
                    "append_dispatch_rejected",
                    "Chat history append dispatch was rejected.");
            return;
        }

        _logger.LogWarning(
            "Chat turn history delivery append dispatched event persisting: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} conversationActorId={ConversationActorId} sourceCommandId={SourceCommandId} appendAttempt={AppendAttempt}",
            Id,
            State.DeliveryId,
            conversationActorId,
            State.SourceCommandId,
            appendAttempt);
        var appendDispatchedPersistStarted = Stopwatch.GetTimestamp();
        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryAppendDispatchedEvent
        {
            DeliveryId = State.DeliveryId,
            SourceActorId = State.SourceActorId,
            SourceCommandId = State.SourceCommandId,
            AppendAttempt = appendAttempt,
            TerminalStatus = State.TerminalStatus,
            TerminalText = State.TerminalText,
            TerminalErrorCode = State.TerminalErrorCode,
            TerminalObservedAtUnixMs = State.TerminalObservedAtUnixMs,
            DispatchedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        _logger.LogWarning(
            "Chat turn history delivery append dispatched event persisted: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} sourceCommandId={SourceCommandId} appendAttempt={AppendAttempt} currentStatus={CurrentStatus} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.SourceCommandId,
            appendAttempt,
            State.Status,
            Stopwatch.GetElapsedTime(appendDispatchedPersistStarted).TotalMilliseconds);
    }

    [EventHandler]
    public async Task HandleAbandonedAsync(ChatTurnHistoryDeliveryAbandonedEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(State.DeliveryId, command.DeliveryId, StringComparison.Ordinal))
            return;
        if (State.Status is ChatTurnHistoryDeliveryStatus.AppendDispatched
            or ChatTurnHistoryDeliveryStatus.Abandoned
            or ChatTurnHistoryDeliveryStatus.Failed)
        {
            return;
        }

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryAbandonedEvent
        {
            DeliveryId = State.DeliveryId,
            Reason = string.IsNullOrWhiteSpace(command.Reason)
                ? "workflow_dispatch_not_accepted"
                : command.Reason.Trim(),
            AbandonedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private AppendChatTurnCommand BuildAppendCommandFromState()
    {
        var sanitizedError = State.TerminalStatus is
            ChatTurnTerminalStatus.Failed or ChatTurnTerminalStatus.OutcomeUncertain
            ? SanitizeError(State.TerminalText, State.TerminalErrorCode)
            : State.TerminalStatus == ChatTurnTerminalStatus.Stopped
                ? SanitizeError(string.Empty, State.TerminalErrorCode)
                : string.Empty;

        return new AppendChatTurnCommand
        {
            ScopeId = State.ScopeId,
            ConversationId = State.ConversationId,
            DeliveryActorId = Id,
            Turn = new ChatTurn
            {
                TurnId = State.TurnId,
                UserText = State.UserText,
                AssistantText = State.TerminalStatus is
                    ChatTurnTerminalStatus.Completed or
                    ChatTurnTerminalStatus.Blocked or
                    ChatTurnTerminalStatus.OutcomeUncertain
                    ? State.TerminalText?.Trim() ?? string.Empty
                    : string.Empty,
                TerminalStatus = State.TerminalStatus,
                SanitizedError = sanitizedError,
                TerminalTime = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.FromUnixTimeMilliseconds(State.TerminalObservedAtUnixMs)),
                Operations = { State.TerminalOperations.Select(operation => operation.Clone()) },
            },
        };
    }

    [EventHandler]
    public async Task HandleAppendResultObservedAsync(ChatTurnHistoryDeliveryAppendResultObserved command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(Id, command.DeliveryActorId, StringComparison.Ordinal) ||
            !string.Equals(State.ConversationId, command.ConversationId, StringComparison.Ordinal) ||
            !string.Equals(State.TurnId, command.TurnId, StringComparison.Ordinal))
        {
            return;
        }

        if (State.Status is ChatTurnHistoryDeliveryStatus.AppendCommitted
            or ChatTurnHistoryDeliveryStatus.AppendRejected
            or ChatTurnHistoryDeliveryStatus.Abandoned
            or ChatTurnHistoryDeliveryStatus.Failed)
        {
            return;
        }

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryAppendResultRecordedEvent
        {
            DeliveryActorId = command.DeliveryActorId,
            ConversationId = command.ConversationId,
            TurnId = command.TurnId,
            Accepted = command.Accepted,
            RejectionReason = command.RejectionReason,
            ObservedAtUnixMs = command.ObservedAtUnixMs > 0
                ? command.ObservedAtUnixMs
                : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task PersistFailureAsync(
        string deliveryId,
        string sourceActorId,
        string sourceCommandId,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryFailedEvent
        {
            DeliveryId = deliveryId,
            SourceActorId = sourceActorId,
            SourceCommandId = sourceCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private bool IsCurrentSource(string? deliveryId, string? sourceActorId, string? sourceCommandId) =>
        string.Equals(State.DeliveryId, deliveryId, StringComparison.Ordinal) &&
        string.Equals(State.SourceActorId, sourceActorId, StringComparison.Ordinal) &&
        string.Equals(State.SourceCommandId, sourceCommandId, StringComparison.Ordinal);

    private static bool IsValidNotificationEnvelope(
        WorkflowRunTerminalNotification notification,
        string publisherActorId) =>
        !string.IsNullOrWhiteSpace(publisherActorId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowActorId) &&
        string.Equals(publisherActorId, notification.WorkflowActorId.Trim(), StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(notification.DeliveryId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowCommandId) &&
        notification.Status != WorkflowRunTerminalStatus.Unspecified;

    private static bool IsValidSourceTerminal(
        ChatTurnHistorySourceTerminalNotified notification,
        string publisherActorId) =>
        !string.IsNullOrWhiteSpace(publisherActorId) &&
        !string.IsNullOrWhiteSpace(notification.SourceActorId) &&
        string.Equals(publisherActorId, notification.SourceActorId.Trim(), StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(notification.DeliveryId) &&
        !string.IsNullOrWhiteSpace(notification.SourceCommandId) &&
        notification.Status != ChatTurnTerminalStatus.Unspecified &&
        notification.ObservedAtUnixMs > 0;

    private long ResolveTerminalObservedAt(WorkflowRunTerminalNotification notification)
    {
        try
        {
            return notification.TerminalAt?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                   ?? _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        }
        catch (InvalidOperationException)
        {
            return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        }
    }

    private static ChatTurnTerminalStatus ToChatTurnTerminalStatus(WorkflowRunTerminalStatus status) =>
        status switch
        {
            WorkflowRunTerminalStatus.Completed => ChatTurnTerminalStatus.Completed,
            WorkflowRunTerminalStatus.Failed => ChatTurnTerminalStatus.Failed,
            WorkflowRunTerminalStatus.Stopped => ChatTurnTerminalStatus.Stopped,
            _ => ChatTurnTerminalStatus.Unspecified,
        };

    private static string ResolveTerminalText(WorkflowRunTerminalNotification notification) =>
        notification.Status switch
        {
            WorkflowRunTerminalStatus.Completed => notification.Output?.Trim() ?? string.Empty,
            WorkflowRunTerminalStatus.Failed => string.IsNullOrWhiteSpace(notification.Error)
                ? "Workflow failed."
                : notification.Error.Trim(),
            WorkflowRunTerminalStatus.Stopped => string.Empty,
            _ => string.Empty,
        };

    private static string ResolveTerminalErrorCode(WorkflowRunTerminalNotification notification) =>
        notification.Status switch
        {
            WorkflowRunTerminalStatus.Failed => "workflow_run_error",
            WorkflowRunTerminalStatus.Stopped => string.IsNullOrWhiteSpace(notification.Error)
                ? "workflow_run_stopped"
                : notification.Error.Trim(),
            _ => string.Empty,
        };

    private static bool HasSameTerminalFrame(
        ChatTurnHistoryDeliveryState state,
        ChatTurnHistoryDeliveryTerminalFrameObserved terminal) =>
        state.TerminalStatus == terminal.Status &&
        string.Equals(state.TerminalText, terminal.Text, StringComparison.Ordinal) &&
        string.Equals(state.TerminalErrorCode, terminal.ErrorCode, StringComparison.Ordinal) &&
        state.TerminalObservedAtUnixMs == terminal.ObservedAtUnixMs;

    private static bool CanReconcileTerminal(
        ChatTurnTerminalStatus current,
        ChatTurnTerminalStatus candidate) =>
        current == ChatTurnTerminalStatus.OutcomeUncertain &&
        candidate is ChatTurnTerminalStatus.Completed or ChatTurnTerminalStatus.Failed;

    private static bool HasSameReservation(
        ChatTurnHistoryDeliveryState state,
        ChatTurnHistoryDeliveryReserveRequested command) =>
        string.Equals(state.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ScopeId, command.ScopeId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ConversationId, command.ConversationId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.TurnId, command.TurnId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.UserText, command.UserText?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.SourceActorId, command.SourceActorId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.SourceCommandId, command.SourceCommandId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.SourceCorrelationId, command.SourceCorrelationId?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        state.CreateConversationIfMissing == command.CreateConversationIfMissing &&
        string.Equals(state.RequestFingerprint, command.RequestFingerprint?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        state.ExposeCreateRecovery == command.ExposeCreateRecovery;

    private static (string Code, string Summary)? ValidateReserve(ChatTurnHistoryDeliveryReserveRequested command)
    {
        if (string.IsNullOrWhiteSpace(command.DeliveryId))
            return ("delivery_id_required", "Chat history delivery id is required.");
        if (string.IsNullOrWhiteSpace(command.ScopeId))
            return ("scope_id_required", "Chat history delivery requires a scope id.");
        if (string.IsNullOrWhiteSpace(command.ConversationId))
            return ("conversation_id_required", "Chat history delivery requires a conversation id.");
        if (string.IsNullOrWhiteSpace(command.TurnId))
            return ("turn_id_required", "Chat history delivery requires a turn id.");
        if (string.IsNullOrWhiteSpace(command.UserText))
            return ("user_text_required", "Chat history delivery requires user text.");
        if (string.IsNullOrWhiteSpace(command.SourceActorId))
            return ("source_actor_id_required", "Chat history delivery requires a source actor id.");
        if (string.IsNullOrWhiteSpace(command.SourceCommandId))
            return ("source_command_id_required", "Chat history delivery requires a source command id.");
        return null;
    }

    private static string SanitizeError(string? text, string? errorCode)
    {
        var message = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        var code = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : errorCode.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return code;
        if (string.IsNullOrWhiteSpace(code))
            return message;
        return $"{code}: {message}";
    }

    private static ChatTurnHistoryDeliveryState ApplyReserved(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryReservedEvent evt)
    {
        var next = current.Clone();
        next.DeliveryId = evt.DeliveryId;
        next.ScopeId = evt.ScopeId;
        next.ConversationId = evt.ConversationId;
        next.TurnId = evt.TurnId;
        next.UserText = evt.UserText;
        next.SourceActorId = evt.SourceActorId;
        next.SourceCommandId = evt.SourceCommandId;
        next.SourceCorrelationId = evt.SourceCorrelationId;
        next.RequestFingerprint = evt.RequestFingerprint;
        next.Status = ChatTurnHistoryDeliveryStatus.Reserved;
        next.ReservedAtUnixMs = evt.ReservedAtUnixMs;
        next.CreateConversationIfMissing = evt.CreateConversationIfMissing;
        next.ExposeCreateRecovery = evt.ExposeCreateRecovery;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyBound(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryBoundEvent evt)
    {
        var next = current.Clone();
        next.Status = ChatTurnHistoryDeliveryStatus.Bound;
        next.BoundAtUnixMs = evt.BoundAtUnixMs;
        next.SourceCorrelationId = evt.SourceCorrelationId;
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyTerminalFrameObserved(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryTerminalFrameObserved evt)
    {
        var next = current.Clone();
        next.TerminalStatus = evt.Status;
        next.TerminalText = evt.Text;
        next.TerminalErrorCode = evt.ErrorCode;
        next.TerminalObservedAtUnixMs = evt.ObservedAtUnixMs;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        ReplaceTerminalOperations(next, evt.Operations);
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyTerminalReconciled(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryTerminalReconciledEvent evt)
    {
        if (current.TerminalStatus != evt.PreviousStatus ||
            !CanReconcileTerminal(current.TerminalStatus, evt.Status))
        {
            return current;
        }

        var next = current.Clone();
        next.Status = ChatTurnHistoryDeliveryStatus.TerminalReconciliationPrepared;
        next.TerminalStatus = evt.Status;
        next.TerminalText = evt.Text;
        next.TerminalErrorCode = evt.ErrorCode;
        next.TerminalObservedAtUnixMs = evt.ObservedAtUnixMs;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        ReplaceTerminalOperations(next, evt.Operations);
        return next;
    }

    /// <summary>
    /// Replaces the recorded ledger only when the source reported one, so a
    /// reconciliation that carries no operations cannot erase the observed ledger.
    /// </summary>
    private static void ReplaceTerminalOperations(
        ChatTurnHistoryDeliveryState state,
        IEnumerable<ChatTurnOperation> operations)
    {
        var replacement = operations.Select(operation => operation.Clone()).ToList();
        if (replacement.Count == 0)
            return;
        state.TerminalOperations.Clear();
        state.TerminalOperations.AddRange(replacement);
    }

    private static ChatTurnHistoryDeliveryState ApplyAppendDispatched(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryAppendDispatchedEvent evt)
    {
        var next = current.Clone();
        next.Status = ChatTurnHistoryDeliveryStatus.AppendDispatched;
        next.CompletedAtUnixMs = evt.DispatchedAtUnixMs;
        next.AppendAttempt = evt.AppendAttempt;
        next.TerminalStatus = evt.TerminalStatus;
        next.TerminalText = evt.TerminalText;
        next.TerminalErrorCode = evt.TerminalErrorCode;
        next.TerminalObservedAtUnixMs = evt.TerminalObservedAtUnixMs;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyAppendResultRecorded(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryAppendResultRecordedEvent evt)
    {
        var next = current.Clone();
        next.Status = evt.Accepted
            ? ChatTurnHistoryDeliveryStatus.AppendCommitted
            : ChatTurnHistoryDeliveryStatus.AppendRejected;
        next.CompletedAtUnixMs = evt.ObservedAtUnixMs;
        next.AppendRejectionReason = evt.RejectionReason;
        if (evt.Accepted)
        {
            next.ErrorCode = string.Empty;
            next.ErrorSummary = string.Empty;
        }
        else
        {
            next.ErrorCode = evt.RejectionReason.ToString();
            next.ErrorSummary = evt.RejectionReason.ToString();
        }
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyAbandoned(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryAbandonedEvent evt)
    {
        var next = current.Clone();
        next.Status = ChatTurnHistoryDeliveryStatus.Abandoned;
        next.CompletedAtUnixMs = evt.AbandonedAtUnixMs;
        next.ErrorCode = evt.Reason;
        next.ErrorSummary = evt.Reason;
        return next;
    }

    private static ChatTurnHistoryDeliveryState ApplyFailed(
        ChatTurnHistoryDeliveryState current,
        ChatTurnHistoryDeliveryFailedEvent evt)
    {
        var next = current.Clone();
        next.Status = ChatTurnHistoryDeliveryStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        return next;
    }
}
