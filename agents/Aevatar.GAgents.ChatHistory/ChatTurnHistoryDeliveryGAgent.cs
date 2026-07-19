using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatHistory;

[GAgent("chat.history.turn-delivery")]
public sealed class ChatTurnHistoryDeliveryGAgent : GAgentBase<ChatTurnHistoryDeliveryState>
{
    private const string ConversationAppendPublisherId = "chat-history-turn-delivery";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly TimeProvider _timeProvider;

    public ChatTurnHistoryDeliveryGAgent(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
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
            .On<ChatTurnHistoryDeliveryAppendDispatchedEvent>(ApplyAppendDispatched)
            .On<ChatTurnHistoryDeliveryAppendResultRecordedEvent>(ApplyAppendResultRecorded)
            .On<ChatTurnHistoryDeliveryAbandonedEvent>(ApplyAbandoned)
            .On<ChatTurnHistoryDeliveryFailedEvent>(ApplyFailed)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await DispatchPendingTerminalAppendAsync(ct).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleReserveAsync(ChatTurnHistoryDeliveryReserveRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Status is ChatTurnHistoryDeliveryStatus.AppendDispatched
            or ChatTurnHistoryDeliveryStatus.Abandoned
            or ChatTurnHistoryDeliveryStatus.Failed)
        {
            return;
        }

        if (State.Status is ChatTurnHistoryDeliveryStatus.Reserved or ChatTurnHistoryDeliveryStatus.Bound)
            return;

        var validation = ValidateReserve(command);
        if (validation is not null)
        {
            await PersistFailureAsync(
                    command.DeliveryId ?? string.Empty,
                    command.WorkflowActorId ?? string.Empty,
                    command.WorkflowCommandId ?? string.Empty,
                    validation.Value.Code,
                    validation.Value.Summary)
                .ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryReservedEvent
        {
            DeliveryId = command.DeliveryId.Trim(),
            ScopeId = command.ScopeId.Trim(),
            ConversationId = command.ConversationId.Trim(),
            TurnId = command.TurnId.Trim(),
            UserText = command.UserText.Trim(),
            WorkflowActorId = command.WorkflowActorId.Trim(),
            WorkflowCommandId = command.WorkflowCommandId.Trim(),
            WorkflowCorrelationId = command.WorkflowCorrelationId?.Trim() ?? string.Empty,
            ReservedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            CreateConversationIfMissing = command.CreateConversationIfMissing,
        });
    }

    [EventHandler]
    public async Task HandleAcceptedBoundAsync(ChatTurnHistoryDeliveryAcceptedBound command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsCurrentWorkflow(command.DeliveryId, command.WorkflowActorId, command.WorkflowCommandId))
            return;
        if (State.Status != ChatTurnHistoryDeliveryStatus.Reserved)
            return;

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryBoundEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            WorkflowCorrelationId = string.IsNullOrWhiteSpace(command.WorkflowCorrelationId)
                ? State.WorkflowCorrelationId
                : command.WorkflowCorrelationId.Trim(),
            BoundAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        await DispatchPendingTerminalAppendAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleTerminalNotificationAsync(WorkflowRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (State.Status is not (ChatTurnHistoryDeliveryStatus.Reserved or ChatTurnHistoryDeliveryStatus.Bound))
            return;

        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
        if (!IsValidNotificationEnvelope(notification, publisherActorId) ||
            !IsCurrentWorkflow(notification.DeliveryId, notification.WorkflowActorId, notification.WorkflowCommandId))
        {
            return;
        }

        var terminal = ToTerminalFrameObserved(notification);
        if (State.TerminalStatus != ChatTurnTerminalStatus.Unspecified)
        {
            if (HasSameTerminalFrame(State, terminal))
                await DispatchPendingTerminalAppendAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(terminal);
        await DispatchPendingTerminalAppendAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DispatchPendingTerminalAppendAsync(CancellationToken ct)
    {
        if (State.Status is not (ChatTurnHistoryDeliveryStatus.Reserved or ChatTurnHistoryDeliveryStatus.Bound) ||
            State.TerminalStatus == ChatTurnTerminalStatus.Unspecified)
        {
            return;
        }

        var appendCommand = BuildAppendCommandFromState();
        var conversationActorId = ChatHistoryActorIds.Conversation(State.ScopeId, State.ConversationId);
        if (!await _actorRuntime.ExistsAsync(conversationActorId).ConfigureAwait(false))
        {
            if (!State.CreateConversationIfMissing)
            {
                await PersistFailureAsync(
                        State.DeliveryId,
                        State.WorkflowActorId,
                        State.WorkflowCommandId,
                        "conversation_not_found",
                        "Chat history conversation was not found.")
                    .ConfigureAwait(false);
                return;
            }

            await _actorRuntime.CreateAsync<ChatConversationGAgent>(conversationActorId, ct)
                .ConfigureAwait(false);
        }

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(appendCommand),
            Route = EnvelopeRouteSemantics.CreateDirect(ConversationAppendPublisherId, conversationActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(State.WorkflowCorrelationId)
                    ? State.WorkflowCommandId
                    : State.WorkflowCorrelationId,
            },
        };
        envelope.EnsureRuntime().EnsureDeduplication().OperationId = $"chat-history-append:{State.DeliveryId}";

        var admission = await _dispatchPort.DispatchAsync(conversationActorId, envelope, ct)
            .ConfigureAwait(false);
        if (!admission.Accepted)
        {
            await PersistFailureAsync(
                    State.DeliveryId,
                    State.WorkflowActorId,
                    State.WorkflowCommandId,
                    "append_dispatch_rejected",
                    "Chat history append dispatch was rejected.")
                .ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryAppendDispatchedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            AppendAttempt = Math.Max(1, State.AppendAttempt + 1),
            TerminalStatus = State.TerminalStatus,
            TerminalText = State.TerminalText,
            TerminalErrorCode = State.TerminalErrorCode,
            TerminalObservedAtUnixMs = State.TerminalObservedAtUnixMs,
            DispatchedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
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
        var sanitizedError = State.TerminalStatus == ChatTurnTerminalStatus.Failed
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
                AssistantText = State.TerminalStatus == ChatTurnTerminalStatus.Completed
                    ? State.TerminalText?.Trim() ?? string.Empty
                    : string.Empty,
                TerminalStatus = State.TerminalStatus,
                SanitizedError = sanitizedError,
                TerminalTime = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.FromUnixTimeMilliseconds(State.TerminalObservedAtUnixMs)),
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
        string workflowActorId,
        string workflowCommandId,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new ChatTurnHistoryDeliveryFailedEvent
        {
            DeliveryId = deliveryId,
            WorkflowActorId = workflowActorId,
            WorkflowCommandId = workflowCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private bool IsCurrentWorkflow(string? deliveryId, string? workflowActorId, string? workflowCommandId) =>
        string.Equals(State.DeliveryId, deliveryId, StringComparison.Ordinal) &&
        string.Equals(State.WorkflowActorId, workflowActorId, StringComparison.Ordinal) &&
        string.Equals(State.WorkflowCommandId, workflowCommandId, StringComparison.Ordinal);

    private static bool IsValidNotificationEnvelope(
        WorkflowRunTerminalNotification notification,
        string publisherActorId) =>
        !string.IsNullOrWhiteSpace(publisherActorId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowActorId) &&
        string.Equals(publisherActorId, notification.WorkflowActorId.Trim(), StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(notification.DeliveryId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowCommandId) &&
        notification.Status != WorkflowRunTerminalStatus.Unspecified;

    private ChatTurnHistoryDeliveryTerminalFrameObserved ToTerminalFrameObserved(
        WorkflowRunTerminalNotification notification) =>
        new()
        {
            DeliveryId = notification.DeliveryId.Trim(),
            WorkflowActorId = notification.WorkflowActorId.Trim(),
            WorkflowCommandId = notification.WorkflowCommandId.Trim(),
            Status = ToChatTurnTerminalStatus(notification.Status),
            Text = ResolveTerminalText(notification),
            ErrorCode = ResolveTerminalErrorCode(notification),
            ObservedAtUnixMs = ResolveTerminalObservedAt(notification),
        };

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
        if (string.IsNullOrWhiteSpace(command.WorkflowActorId))
            return ("workflow_actor_id_required", "Chat history delivery requires a workflow actor id.");
        if (string.IsNullOrWhiteSpace(command.WorkflowCommandId))
            return ("workflow_command_id_required", "Chat history delivery requires a workflow command id.");
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
        next.WorkflowActorId = evt.WorkflowActorId;
        next.WorkflowCommandId = evt.WorkflowCommandId;
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId;
        next.Status = ChatTurnHistoryDeliveryStatus.Reserved;
        next.ReservedAtUnixMs = evt.ReservedAtUnixMs;
        next.CreateConversationIfMissing = evt.CreateConversationIfMissing;
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
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId;
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
        return next;
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
