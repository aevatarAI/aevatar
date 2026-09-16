using System.Diagnostics;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

[GAgent("nyxid.chat.workflow-run-delivery")]
public sealed class WorkflowRunDeliveryGAgent : GAgentBase<WorkflowRunDeliveryGAgentState>
{
    private const string ChannelWorkflowDeliveryUnavailableCode = "channel_workflow_delivery_unavailable";
    private const string ChannelWorkflowDeliveryUnavailableSummary =
        "This channel bot is not configured for workflow result delivery.";
    private const string ReservationExpiredCode = "workflow_run_delivery_reservation_expired";
    private const string ToolApprovalRetryCallbackPrefix = "workflow-tool-approval-delivery-retry";
    private const int ToolApprovalInitialRetryDelayMs = 250;
    private const int ToolApprovalMaxRetryDelayMs = 30_000;
    private static readonly TimeSpan MaxDurableTimeoutDelay =
        TimeSpan.FromMilliseconds(int.MaxValue - 1_000L);
    private readonly NyxIdRelayOutboundPort _outboundPort;
    private readonly IInteractiveReplyDispatcher _interactiveReplyDispatcher;
    private readonly IWorkflowResultDeliveryCredentialResolver _credentialResolver;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowRunDeliveryGAgent> _logger;

    public WorkflowRunDeliveryGAgent(
        NyxIdRelayOutboundPort outboundPort,
        IInteractiveReplyDispatcher interactiveReplyDispatcher,
        IWorkflowResultDeliveryCredentialResolver credentialResolver,
        IActorRuntimeCallbackScheduler callbackScheduler,
        ILogger<WorkflowRunDeliveryGAgent> logger,
        TimeProvider? timeProvider = null)
    {
        _outboundPort = outboundPort ?? throw new ArgumentNullException(nameof(outboundPort));
        _interactiveReplyDispatcher = interactiveReplyDispatcher ??
            throw new ArgumentNullException(nameof(interactiveReplyDispatcher));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _callbackScheduler = callbackScheduler ?? throw new ArgumentNullException(nameof(callbackScheduler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override WorkflowRunDeliveryGAgentState TransitionState(
        WorkflowRunDeliveryGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunDeliveryReservedEvent>(ApplyReserved)
            .On<WorkflowRunDeliveryStartedEvent>(ApplyStarted)
            .On<WorkflowRunDeliveryTerminalNotificationBufferedEvent>(ApplyTerminalBuffered)
            .On<WorkflowRunDeliveryTerminalNotificationDiscardedEvent>(ApplyTerminalDiscarded)
            .On<WorkflowRunDeliveryToolApprovalNotificationBufferedEvent>(ApplyToolApprovalBuffered)
            .On<WorkflowRunDeliveryToolApprovalNotificationDeliveredEvent>(ApplyToolApprovalDelivered)
            .On<WorkflowRunDeliveryToolApprovalNotificationRetryScheduledEvent>(ApplyToolApprovalRetryScheduled)
            .On<WorkflowRunDeliveryToolApprovalNotificationDiscardedEvent>(ApplyToolApprovalDiscarded)
            .On<WorkflowRunDeliverySucceededEvent>(ApplySucceeded)
            .On<WorkflowRunDeliveryFailedEvent>(ApplyFailed)
            .On<WorkflowRunDeliveryAbandonedEvent>(ApplyAbandoned)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (IsTerminal(State.Status))
        {
            await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
            return;
        }

        if (State.PendingTerminalNotification is not null &&
            State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started)
        {
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
            await TryDeliverPendingTerminalAsync(ct).ConfigureAwait(false);
            return;
        }

        if (State.PendingToolApprovalNotification is not null &&
            State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started)
        {
            await PromoteReservedToolApprovalAsync().ConfigureAwait(false);
            await TryDeliverPendingToolApprovalAsync(ct).ConfigureAwait(false);
            return;
        }

        if (State.PendingTerminalNotification is null &&
            State.PendingToolApprovalNotification is null &&
            State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started)
        {
            await EnsureReservationExpiryAsync(ct).ConfigureAwait(false);
        }
    }

    [EventHandler]
    public async Task HandleReserveAsync(WorkflowRunDeliveryReserveRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsTerminal(State.Status))
            return;

        var validationError = ValidateReservation(command);
        if (validationError is not null)
        {
            await PersistInitialFailureAsync(
                    command.DeliveryId,
                    command.ExpectedWorkflowCommandId,
                    validationError.Value.Code,
                    validationError.Value.Summary)
                .ConfigureAwait(false);
            return;
        }

        if (State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started)
        {
            if (IsSameReservation(command))
            {
                if (State.PendingTerminalNotification is not null)
                    await ReconcilePendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
                else if (State.PendingToolApprovalNotification is not null)
                    await ReconcilePendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
                else
                    await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryReservedEvent
        {
            DeliveryId = command.DeliveryId.Trim(),
            ExpectedWorkflowCommandId = command.ExpectedWorkflowCommandId.Trim(),
            ChannelPlatform = command.ChannelPlatform.Trim(),
            ReplyMessageId = command.ReplyMessageId.Trim(),
            PlatformMessageId = command.PlatformMessageId?.Trim() ?? string.Empty,
            RegistrationScopeId = command.RegistrationScopeId?.Trim() ?? string.Empty,
            WorkflowResultDeliveryCredential = command.WorkflowResultDeliveryCredential.Clone(),
            BotRegistrationId = command.BotRegistrationId?.Trim() ?? string.Empty,
            ExpiresAtUnixMs = command.ExpiresAtUnixMs,
            ReservedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        if (State.PendingTerminalNotification is not null && !PendingMatchesReservation())
        {
            await DiscardPendingTerminalAsync("reservation_identity_mismatch").ConfigureAwait(false);
        }
        if (State.PendingToolApprovalNotification is not null && !PendingToolApprovalMatchesReservation())
        {
            await DiscardPendingToolApprovalAsync("reservation_identity_mismatch").ConfigureAwait(false);
        }

        if (State.PendingTerminalNotification is not null)
        {
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
            await TryDeliverPendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else if (State.PendingToolApprovalNotification is not null)
        {
            await PromoteReservedToolApprovalAsync().ConfigureAwait(false);
            await TryDeliverPendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [EventHandler]
    public async Task HandleStartAsync(WorkflowRunDeliveryStartRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsTerminal(State.Status) || State.Status == WorkflowRunDeliveryStatus.Unspecified)
            return;

        if (!IsValidStartIdentity(command))
        {
            _logger.LogWarning(
                "Ignoring workflow run delivery start with mismatched reservation identity: deliveryId={DeliveryId} workflowActorId={WorkflowActorId} commandId={CommandId}",
                command.DeliveryId,
                command.WorkflowActorId,
                command.WorkflowCommandId);
            return;
        }

        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
        {
            await PersistDomainEventAsync(new WorkflowRunDeliveryStartedEvent
            {
                DeliveryId = State.DeliveryId,
                WorkflowActorId = command.WorkflowActorId.Trim(),
                WorkflowRunId = command.WorkflowRunId?.Trim() ?? string.Empty,
                WorkflowCommandId = command.WorkflowCommandId.Trim(),
                WorkflowCorrelationId = command.WorkflowCorrelationId?.Trim() ?? string.Empty,
                StreamTopic = command.StreamTopic?.Trim() ?? string.Empty,
                StartedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
        }

        if (State.PendingTerminalNotification is not null && !PendingMatchesStartedRun())
            await DiscardPendingTerminalAsync("workflow_actor_identity_mismatch").ConfigureAwait(false);
        if (State.PendingToolApprovalNotification is not null && !PendingToolApprovalMatchesStartedRun())
            await DiscardPendingToolApprovalAsync("workflow_actor_identity_mismatch").ConfigureAwait(false);

        if (State.PendingTerminalNotification is not null)
            await TryDeliverPendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        else if (State.PendingToolApprovalNotification is not null)
            await TryDeliverPendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
        else
            await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleToolApprovalNotificationAsync(WorkflowRunToolApprovalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (IsTerminal(State.Status))
            return;

        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
        if (!IsValidToolApprovalNotificationEnvelope(notification, publisherActorId))
            return;
        if (State.DeliveredToolApprovalRequestIds.Contains(notification.ApprovalRequestId, StringComparer.Ordinal))
            return;
        if (State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started &&
            (!string.Equals(State.DeliveryId, notification.DeliveryId, StringComparison.Ordinal) ||
             !string.Equals(State.ExpectedWorkflowCommandId, notification.WorkflowCommandId, StringComparison.Ordinal)))
        {
            return;
        }
        if (State.Status == WorkflowRunDeliveryStatus.Started &&
            !string.Equals(State.WorkflowActorId, notification.WorkflowActorId, StringComparison.Ordinal))
        {
            return;
        }

        if (State.PendingTerminalNotification is not null)
            return;
        if (State.PendingToolApprovalNotification is not null)
        {
            if (SameToolApprovalIdentity(State.PendingToolApprovalNotification, notification))
                await ReconcilePendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryToolApprovalNotificationBufferedEvent
        {
            DeliveryId = notification.DeliveryId,
            WorkflowCommandId = notification.WorkflowCommandId,
            PublisherActorId = publisherActorId,
            Notification = notification.Clone(),
            BufferedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
            await PromoteReservedToolApprovalAsync().ConfigureAwait(false);
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await TryDeliverPendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleTerminalNotificationAsync(WorkflowRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (IsTerminal(State.Status))
            return;

        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
        _logger.LogWarning(
            "Workflow run delivery terminal notification received: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowActorId={WorkflowActorId} publisherActorId={PublisherActorId} workflowCommandId={WorkflowCommandId} status={TerminalStatus} currentStatus={CurrentStatus} pendingTerminal={PendingTerminal}",
            Id,
            notification.DeliveryId,
            notification.WorkflowActorId,
            publisherActorId,
            notification.WorkflowCommandId,
            notification.Status,
            State.Status,
            State.PendingTerminalNotification is not null);
        if (!IsValidNotificationEnvelope(notification, publisherActorId))
            return;
        if (State.Status is WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started &&
            (!string.Equals(State.DeliveryId, notification.DeliveryId, StringComparison.Ordinal) ||
             !string.Equals(
                 State.ExpectedWorkflowCommandId,
                 notification.WorkflowCommandId,
                 StringComparison.Ordinal)))
        {
            return;
        }
        if (State.Status == WorkflowRunDeliveryStatus.Started &&
            !string.Equals(State.WorkflowActorId, notification.WorkflowActorId, StringComparison.Ordinal))
        {
            return;
        }

        if (State.PendingToolApprovalNotification is not null)
            await DiscardPendingToolApprovalAsync("terminal_notification_received").ConfigureAwait(false);

        if (State.PendingTerminalNotification is not null)
        {
            if (SameTerminalIdentity(State.PendingTerminalNotification, notification))
            {
                await ReconcilePendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (State.Status != WorkflowRunDeliveryStatus.Unspecified)
                return;
        }

        var terminalBufferPersistStarted = Stopwatch.GetTimestamp();
        await PersistDomainEventAsync(new WorkflowRunDeliveryTerminalNotificationBufferedEvent
        {
            DeliveryId = notification.DeliveryId,
            WorkflowCommandId = notification.WorkflowCommandId,
            PublisherActorId = publisherActorId,
            Notification = notification.Clone(),
            BufferedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        _logger.LogWarning(
            "Workflow run delivery terminal notification buffered: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowActorId={WorkflowActorId} workflowCommandId={WorkflowCommandId} currentStatus={CurrentStatus} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.WorkflowActorId,
            State.ExpectedWorkflowCommandId,
            State.Status,
            Stopwatch.GetElapsedTime(terminalBufferPersistStarted).TotalMilliseconds);

        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await TryDeliverPendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleAbandonAsync(WorkflowRunDeliveryAbandonRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsTerminal(State.Status) || State.Status == WorkflowRunDeliveryStatus.Unspecified)
            return;
        if (!string.Equals(State.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) ||
            !string.Equals(State.ExpectedWorkflowCommandId, command.WorkflowCommandId?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryAbandonedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowCommandId = State.ExpectedWorkflowCommandId,
            Reason = string.IsNullOrWhiteSpace(command.Reason)
                ? "Workflow dispatch was not accepted."
                : command.Reason.Trim(),
            AbandonedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleToolApprovalRetryAsync(
        WorkflowRunDeliveryToolApprovalNotificationRetryReached command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = State.PendingToolApprovalNotification;
        if (State.Status != WorkflowRunDeliveryStatus.Started ||
            pending is null ||
            !string.Equals(command.DeliveryId, State.DeliveryId, StringComparison.Ordinal) ||
            !string.Equals(command.WorkflowActorId, State.WorkflowActorId, StringComparison.Ordinal) ||
            !string.Equals(command.WorkflowCommandId, State.WorkflowCommandId, StringComparison.Ordinal) ||
            !string.Equals(command.ApprovalRequestId, pending.ApprovalRequestId, StringComparison.Ordinal) ||
            command.Attempt != State.ToolApprovalDeliveryAttempt)
        {
            return;
        }

        await TryDeliverPendingToolApprovalAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleReservationExpiryAsync(WorkflowRunDeliveryReservationExpiryReached command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Status is not (WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started) ||
            State.PendingTerminalNotification is not null ||
            !string.Equals(State.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) ||
            !string.Equals(State.ExpectedWorkflowCommandId, command.WorkflowCommandId?.Trim(), StringComparison.Ordinal) ||
            State.ReservationExpiresAtUnixMs != command.ExpiresAtUnixMs)
        {
            return;
        }

        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() < State.ReservationExpiresAtUnixMs)
        {
            await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (State.PendingToolApprovalNotification is not null)
            await DiscardPendingToolApprovalAsync("reservation_expired").ConfigureAwait(false);

        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.ExpectedWorkflowCommandId,
            ErrorCode = ReservationExpiredCode,
            ErrorSummary = "Workflow terminal result was not observed before the delivery reservation expired.",
            Attempt = State.DeliveryAttempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
    }

    private async Task TryDeliverPendingTerminalAsync(CancellationToken ct)
    {
        var notification = State.PendingTerminalNotification?.Clone();
        if (State.Status != WorkflowRunDeliveryStatus.Started || notification is null || !PendingMatchesStartedRun())
            return;

        var attempt = Math.Max(1, State.DeliveryAttempt + 1);
        var observedAt = ResolveTerminalObservedAt(notification);
        _logger.LogWarning(
            "Workflow run delivery pending terminal relay starting: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowActorId={WorkflowActorId} workflowCommandId={WorkflowCommandId} attempt={Attempt} terminalStatus={TerminalStatus}",
            Id,
            State.DeliveryId,
            State.WorkflowActorId,
            State.WorkflowCommandId,
            attempt,
            notification.Status);
        var credentialResolveStarted = Stopwatch.GetTimestamp();
        var deliveryAgentKey = await ResolveWorkflowResultDeliveryAgentKeyAsync(notification, attempt, observedAt, ct)
            .ConfigureAwait(false);
        _logger.LogWarning(
            "Workflow run delivery credential resolution completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} resolved={Resolved} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.WorkflowCommandId,
            attempt,
            deliveryAgentKey is not null,
            Stopwatch.GetElapsedTime(credentialResolveStarted).TotalMilliseconds);
        if (deliveryAgentKey is null)
            return;

        _logger.LogWarning(
            "Workflow run delivery outbound relay sending: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} platform={Platform} replyMessageId={ReplyMessageId}",
            Id,
            State.DeliveryId,
            State.WorkflowCommandId,
            attempt,
            State.ChannelPlatform,
            State.ReplyMessageId);
        var outboundStarted = Stopwatch.GetTimestamp();
        var result = await _outboundPort.SendWithAgentKeyAsync(
                State.ChannelPlatform,
                BuildConversationReference(),
                new MessageContent { Text = BuildTerminalReplyText(notification) },
                new OutboundDeliveryContext
                {
                    ReplyMessageId = State.ReplyMessageId,
                    CorrelationId = string.IsNullOrWhiteSpace(State.WorkflowCorrelationId)
                        ? State.WorkflowCommandId
                        : State.WorkflowCorrelationId,
                },
                deliveryAgentKey,
                ct)
            .ConfigureAwait(false);
        _logger.LogWarning(
            "Workflow run delivery outbound relay completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} success={Success} errorCode={ErrorCode} sentActivityId={SentActivityId} platformMessageId={PlatformMessageId} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.WorkflowCommandId,
            attempt,
            result.Success,
            result.ErrorCode ?? string.Empty,
            result.SentActivityId ?? string.Empty,
            result.PlatformMessageId ?? string.Empty,
            Stopwatch.GetElapsedTime(outboundStarted).TotalMilliseconds);

        if (result.Success)
        {
            _logger.LogWarning(
                "Workflow run delivery success event persisting: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt}",
                Id,
                State.DeliveryId,
                State.WorkflowCommandId,
                attempt);
            var successPersistStarted = Stopwatch.GetTimestamp();
            await PersistDomainEventAsync(new WorkflowRunDeliverySucceededEvent
            {
                DeliveryId = State.DeliveryId,
                WorkflowActorId = State.WorkflowActorId,
                WorkflowCommandId = State.WorkflowCommandId,
                SentActivityId = result.SentActivityId,
                PlatformMessageId = result.PlatformMessageId,
                Attempt = attempt,
                DeliveredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                TerminalOutcome = notification.Status,
                TerminalText = ResolveTerminalText(notification),
                TerminalErrorCode = ResolveTerminalErrorCode(notification),
                TerminalObservedAtUnixMs = observedAt,
            });
            _logger.LogWarning(
                "Workflow run delivery success event persisted: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} elapsedMs={ElapsedMs}",
                Id,
                State.DeliveryId,
                State.WorkflowCommandId,
                attempt,
                Stopwatch.GetElapsedTime(successPersistStarted).TotalMilliseconds);
            var purgeStarted = Stopwatch.GetTimestamp();
            await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
            _logger.LogWarning(
                "Workflow run delivery callback purge completed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} elapsedMs={ElapsedMs}",
                Id,
                State.DeliveryId,
                State.WorkflowCommandId,
                attempt,
                Stopwatch.GetElapsedTime(purgeStarted).TotalMilliseconds);
            return;
        }

        _logger.LogWarning(
            "Workflow run delivery failure event persisting: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} errorCode={ErrorCode}",
            Id,
            State.DeliveryId,
            State.WorkflowCommandId,
            attempt,
            string.IsNullOrWhiteSpace(result.ErrorCode) ? "workflow_run_delivery_failed" : result.ErrorCode);
        var failurePersistStarted = Stopwatch.GetTimestamp();
        await PersistDeliveryFailureAsync(
                notification,
                attempt,
                observedAt,
                string.IsNullOrWhiteSpace(result.ErrorCode) ? "workflow_run_delivery_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Workflow terminal reply delivery failed."
                    : result.ErrorMessage)
            .ConfigureAwait(false);
        _logger.LogWarning(
            "Workflow run delivery failure event persisted: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} workflowCommandId={WorkflowCommandId} attempt={Attempt} elapsedMs={ElapsedMs}",
            Id,
            State.DeliveryId,
            State.WorkflowCommandId,
            attempt,
            Stopwatch.GetElapsedTime(failurePersistStarted).TotalMilliseconds);
    }

    private async Task TryDeliverPendingToolApprovalAsync(CancellationToken ct)
    {
        var notification = State.PendingToolApprovalNotification?.Clone();
        if (State.Status != WorkflowRunDeliveryStatus.Started ||
            notification is null ||
            !PendingToolApprovalMatchesStartedRun() ||
            State.PendingTerminalNotification is not null)
        {
            return;
        }

        if (State.ReservationExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            await DiscardPendingToolApprovalAsync("reservation_expired").ConfigureAwait(false);
            await HandleReservationExpiryAsync(new WorkflowRunDeliveryReservationExpiryReached
            {
                DeliveryId = State.DeliveryId,
                WorkflowCommandId = State.ExpectedWorkflowCommandId,
                ExpiresAtUnixMs = State.ReservationExpiresAtUnixMs,
            }).ConfigureAwait(false);
            return;
        }

        var attempt = Math.Max(1, State.ToolApprovalDeliveryAttempt + 1);
        string? deliveryAgentKey;
        try
        {
            deliveryAgentKey = await _credentialResolver.ResolveAsync(
                    State.WorkflowResultDeliveryCredential ?? new ChannelWorkflowResultDeliveryCredential(),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow tool approval credential resolution failed; scheduling retry. deliveryId={DeliveryId} approvalRequestId={ApprovalRequestId} attempt={Attempt}",
                State.DeliveryId,
                notification.ApprovalRequestId,
                attempt);
            await ScheduleToolApprovalRetryAsync(notification, attempt, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(deliveryAgentKey))
        {
            _logger.LogWarning(
                "Workflow tool approval credential is unavailable; scheduling retry. deliveryId={DeliveryId} approvalRequestId={ApprovalRequestId} attempt={Attempt}",
                State.DeliveryId,
                notification.ApprovalRequestId,
                attempt);
            await ScheduleToolApprovalRetryAsync(notification, attempt, ct).ConfigureAwait(false);
            return;
        }

        InteractiveReplyDispatchResult result;
        try
        {
            result = await _interactiveReplyDispatcher.DispatchAsync(
                    ChannelId.From(State.ChannelPlatform),
                    State.ReplyMessageId,
                    deliveryAgentKey,
                    WorkflowRunToolApprovalMessageMapper.ToMessageContent(notification),
                    new ComposeContext { Conversation = BuildConversationReference() },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow tool approval card delivery failed; scheduling retry. deliveryId={DeliveryId} approvalRequestId={ApprovalRequestId} attempt={Attempt}",
                State.DeliveryId,
                notification.ApprovalRequestId,
                attempt);
            await ScheduleToolApprovalRetryAsync(notification, attempt, ct).ConfigureAwait(false);
            return;
        }

        if (result.FellBackToText)
        {
            await PersistToolApprovalDeliveryFailureAsync(
                    attempt,
                    "workflow_tool_approval_interactive_delivery_unsupported",
                    "The channel did not accept an interactive workflow approval card.")
                .ConfigureAwait(false);
            return;
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Workflow tool approval card was not accepted as interactive; scheduling retry. deliveryId={DeliveryId} approvalRequestId={ApprovalRequestId} attempt={Attempt} detail={Detail}",
                State.DeliveryId,
                notification.ApprovalRequestId,
                attempt,
                result.Detail ?? string.Empty);
            await ScheduleToolApprovalRetryAsync(notification, attempt, ct).ConfigureAwait(false);
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryToolApprovalNotificationDeliveredEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowCommandId = State.WorkflowCommandId,
            ApprovalRequestId = notification.ApprovalRequestId,
            Attempt = attempt,
            SentActivityId = result.MessageId ?? string.Empty,
            PlatformMessageId = result.PlatformMessageId ?? string.Empty,
            DeliveredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistToolApprovalDeliveryFailureAsync(
        int attempt,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            Attempt = attempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
    }

    private async Task ScheduleToolApprovalRetryAsync(
        WorkflowRunToolApprovalNotification notification,
        int attempt,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var remainingMs = State.ReservationExpiresAtUnixMs - now.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
        {
            await DiscardPendingToolApprovalAsync("reservation_expired").ConfigureAwait(false);
            return;
        }

        var delay = ResolveToolApprovalRetryDelay(attempt, remainingMs);
        var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
            ToolApprovalRetryCallbackPrefix,
            notification.DeliveryId,
            notification.WorkflowCommandId,
            notification.ApprovalRequestId,
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var retryAt = now.Add(delay);
        try
        {
            await _callbackScheduler.ScheduleTimeoutAsync(
                    new RuntimeCallbackTimeoutRequest
                    {
                        ActorId = Id,
                        CallbackId = callbackId,
                        DueTime = delay,
                        TriggerEnvelope = new EventEnvelope
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Timestamp = Timestamp.FromDateTimeOffset(now),
                            Payload = Any.Pack(new WorkflowRunDeliveryToolApprovalNotificationRetryReached
                            {
                                DeliveryId = notification.DeliveryId,
                                WorkflowActorId = notification.WorkflowActorId,
                                WorkflowCommandId = notification.WorkflowCommandId,
                                ApprovalRequestId = notification.ApprovalRequestId,
                                Attempt = attempt,
                            }),
                            Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self),
                        },
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow tool approval durable retry scheduling failed; pending delivery will recover on activation. deliveryId={DeliveryId} approvalRequestId={ApprovalRequestId} attempt={Attempt}",
                State.DeliveryId,
                notification.ApprovalRequestId,
                attempt);
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryToolApprovalNotificationRetryScheduledEvent
        {
            DeliveryId = notification.DeliveryId,
            WorkflowCommandId = notification.WorkflowCommandId,
            ApprovalRequestId = notification.ApprovalRequestId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAtUnixMs = retryAt.ToUnixTimeMilliseconds(),
        });
    }

    private static TimeSpan ResolveToolApprovalRetryDelay(int attempt, long remainingMs)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var exponentialDelayMs = Math.Min(
            ToolApprovalMaxRetryDelayMs,
            ToolApprovalInitialRetryDelayMs * (1L << exponent));
        return TimeSpan.FromMilliseconds(Math.Max(1L, Math.Min(exponentialDelayMs, remainingMs)));
    }

    private async Task PromoteReservedTerminalAsync()
    {
        var notification = State.PendingTerminalNotification;
        if (State.Status != WorkflowRunDeliveryStatus.Reserved ||
            notification is null ||
            !PendingMatchesReservation() ||
            !string.Equals(
                State.PendingTerminalPublisherActorId,
                notification.WorkflowActorId,
                StringComparison.Ordinal))
        {
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryStartedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = notification.WorkflowActorId,
            WorkflowRunId = notification.WorkflowRunId,
            WorkflowCommandId = notification.WorkflowCommandId,
            WorkflowCorrelationId = notification.WorkflowCorrelationId,
            StreamTopic = string.Empty,
            StartedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task PromoteReservedToolApprovalAsync()
    {
        var notification = State.PendingToolApprovalNotification;
        if (State.Status != WorkflowRunDeliveryStatus.Reserved ||
            notification is null ||
            !PendingToolApprovalMatchesReservation() ||
            !string.Equals(
                State.PendingToolApprovalPublisherActorId,
                notification.WorkflowActorId,
                StringComparison.Ordinal))
        {
            return;
        }

        await PersistDomainEventAsync(new WorkflowRunDeliveryStartedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = notification.WorkflowActorId,
            WorkflowRunId = notification.WorkflowRunId,
            WorkflowCommandId = notification.WorkflowCommandId,
            WorkflowCorrelationId = notification.WorkflowCorrelationId,
            StreamTopic = string.Empty,
            StartedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task ReconcilePendingTerminalAsync(CancellationToken ct)
    {
        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await TryDeliverPendingTerminalAsync(ct).ConfigureAwait(false);
    }

    private async Task ReconcilePendingToolApprovalAsync(CancellationToken ct)
    {
        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
            await PromoteReservedToolApprovalAsync().ConfigureAwait(false);
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await TryDeliverPendingToolApprovalAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveWorkflowResultDeliveryAgentKeyAsync(
        WorkflowRunTerminalNotification notification,
        int attempt,
        long observedAt,
        CancellationToken ct)
    {
        string? agentKey;
        try
        {
            agentKey = await _credentialResolver.ResolveAsync(
                    State.WorkflowResultDeliveryCredential ?? new ChannelWorkflowResultDeliveryCredential(),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workflow run delivery credential resolution failed: deliveryId={DeliveryId} credentialRef={CredentialRef}",
                State.DeliveryId,
                State.WorkflowResultDeliveryCredential?.SecretReference?.Ref);
            await PersistDeliveryFailureAsync(
                    notification,
                    attempt,
                    observedAt,
                    "resolver_unavailable",
                    "Workflow terminal reply credential could not be resolved.")
                .ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(agentKey))
            return agentKey;

        await PersistDeliveryFailureAsync(
                notification,
                attempt,
                observedAt,
                "credential_handle_missing",
                "Workflow terminal reply credential handle is not resolvable.")
            .ConfigureAwait(false);
        return null;
    }

    private async Task PersistDeliveryFailureAsync(
        WorkflowRunTerminalNotification notification,
        int attempt,
        long observedAt,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = State.DeliveryId,
            WorkflowActorId = State.WorkflowActorId,
            WorkflowCommandId = State.WorkflowCommandId,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            Attempt = attempt,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            TerminalOutcome = notification.Status,
            TerminalText = ResolveTerminalText(notification),
            TerminalErrorCode = ResolveTerminalErrorCode(notification),
            TerminalObservedAtUnixMs = observedAt,
        });
        await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
    }

    private async Task PersistInitialFailureAsync(
        string deliveryId,
        string workflowCommandId,
        string errorCode,
        string errorSummary)
    {
        await PersistDomainEventAsync(new WorkflowRunDeliveryFailedEvent
        {
            DeliveryId = deliveryId?.Trim() ?? string.Empty,
            WorkflowCommandId = workflowCommandId?.Trim() ?? string.Empty,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            FailedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
        await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
    }

    private async Task PurgeDurableCallbacksBestEffortAsync()
    {
        try
        {
            await _callbackScheduler.PurgeActorAsync(Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow run delivery callback cleanup failed: deliveryActorId={DeliveryActorId} deliveryId={DeliveryId} status={Status}",
                Id,
                State.DeliveryId,
                State.Status);
        }
    }

    private async Task DiscardPendingTerminalAsync(string reason)
    {
        var pending = State.PendingTerminalNotification;
        if (pending is null)
            return;

        await PersistDomainEventAsync(new WorkflowRunDeliveryTerminalNotificationDiscardedEvent
        {
            DeliveryId = pending.DeliveryId,
            WorkflowCommandId = pending.WorkflowCommandId,
            Reason = reason,
            DiscardedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task DiscardPendingToolApprovalAsync(string reason)
    {
        var pending = State.PendingToolApprovalNotification;
        if (pending is null)
            return;

        await PersistDomainEventAsync(new WorkflowRunDeliveryToolApprovalNotificationDiscardedEvent
        {
            DeliveryId = pending.DeliveryId,
            WorkflowCommandId = pending.WorkflowCommandId,
            ApprovalRequestId = pending.ApprovalRequestId,
            Reason = reason,
            DiscardedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    private async Task EnsureReservationExpiryAsync(CancellationToken ct)
    {
        if (State.PendingTerminalNotification is not null ||
            State.PendingToolApprovalNotification is not null ||
            State.Status is not (WorkflowRunDeliveryStatus.Reserved or WorkflowRunDeliveryStatus.Started))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(State.ReservationExpiresAtUnixMs) - now;
        if (remaining <= TimeSpan.Zero)
        {
            await HandleReservationExpiryAsync(new WorkflowRunDeliveryReservationExpiryReached
            {
                DeliveryId = State.DeliveryId,
                WorkflowCommandId = State.ExpectedWorkflowCommandId,
                ExpiresAtUnixMs = State.ReservationExpiresAtUnixMs,
            });
            return;
        }

        var callbackId = $"workflow-run-delivery-expiry:{State.ExpectedWorkflowCommandId}";
        var dueTime = remaining > MaxDurableTimeoutDelay
            ? MaxDurableTimeoutDelay
            : remaining;
        await _callbackScheduler.ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = Id,
                    CallbackId = callbackId,
                    DueTime = dueTime,
                    TriggerEnvelope = new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Timestamp = Timestamp.FromDateTimeOffset(now),
                        Payload = Any.Pack(new WorkflowRunDeliveryReservationExpiryReached
                        {
                            DeliveryId = State.DeliveryId,
                            WorkflowCommandId = State.ExpectedWorkflowCommandId,
                            ExpiresAtUnixMs = State.ReservationExpiresAtUnixMs,
                        }),
                        Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self),
                    },
                },
                ct)
            .ConfigureAwait(false);
    }

    private bool IsSameReservation(WorkflowRunDeliveryReserveRequested command) =>
        string.Equals(State.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            State.ExpectedWorkflowCommandId,
            command.ExpectedWorkflowCommandId?.Trim(),
            StringComparison.Ordinal);

    private bool IsValidStartIdentity(WorkflowRunDeliveryStartRequested command) =>
        !string.IsNullOrWhiteSpace(command.WorkflowActorId) &&
        !string.IsNullOrWhiteSpace(command.WorkflowCommandId) &&
        string.Equals(State.DeliveryId, command.DeliveryId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            State.ExpectedWorkflowCommandId,
            command.WorkflowCommandId.Trim(),
            StringComparison.Ordinal) &&
        (State.Status != WorkflowRunDeliveryStatus.Started ||
         string.Equals(State.WorkflowActorId, command.WorkflowActorId.Trim(), StringComparison.Ordinal));

    private bool IsValidNotificationEnvelope(
        WorkflowRunTerminalNotification notification,
        string publisherActorId)
    {
        if (string.IsNullOrWhiteSpace(publisherActorId) ||
            string.IsNullOrWhiteSpace(notification.WorkflowActorId) ||
            !string.Equals(publisherActorId, notification.WorkflowActorId.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(notification.DeliveryId) &&
               !string.IsNullOrWhiteSpace(notification.WorkflowCommandId) &&
               notification.Status != WorkflowRunTerminalStatus.Unspecified;
    }

    private static bool IsValidToolApprovalNotificationEnvelope(
        WorkflowRunToolApprovalNotification notification,
        string publisherActorId) =>
        !string.IsNullOrWhiteSpace(publisherActorId) &&
        string.Equals(publisherActorId, notification.WorkflowActorId?.Trim(), StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(notification.DeliveryId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowRunId) &&
        !string.IsNullOrWhiteSpace(notification.WorkflowCommandId) &&
        !string.IsNullOrWhiteSpace(notification.StepId) &&
        !string.IsNullOrWhiteSpace(notification.ExecutionId) &&
        !string.IsNullOrWhiteSpace(notification.ToolName) &&
        !string.IsNullOrWhiteSpace(notification.ToolCallId) &&
        !string.IsNullOrWhiteSpace(notification.ApprovalRequestId);

    private bool PendingMatchesReservation()
    {
        var pending = State.PendingTerminalNotification;
        return pending is not null &&
               string.Equals(State.DeliveryId, pending.DeliveryId, StringComparison.Ordinal) &&
               string.Equals(State.ExpectedWorkflowCommandId, pending.WorkflowCommandId, StringComparison.Ordinal);
    }

    private bool PendingMatchesStartedRun()
    {
        var pending = State.PendingTerminalNotification;
        return PendingMatchesReservation() &&
               pending is not null &&
               string.Equals(State.WorkflowActorId, pending.WorkflowActorId, StringComparison.Ordinal) &&
               string.Equals(State.PendingTerminalPublisherActorId, pending.WorkflowActorId, StringComparison.Ordinal);
    }

    private bool PendingToolApprovalMatchesReservation()
    {
        var pending = State.PendingToolApprovalNotification;
        return pending is not null &&
               string.Equals(State.DeliveryId, pending.DeliveryId, StringComparison.Ordinal) &&
               string.Equals(State.ExpectedWorkflowCommandId, pending.WorkflowCommandId, StringComparison.Ordinal);
    }

    private bool PendingToolApprovalMatchesStartedRun()
    {
        var pending = State.PendingToolApprovalNotification;
        return PendingToolApprovalMatchesReservation() &&
               pending is not null &&
               string.Equals(State.WorkflowActorId, pending.WorkflowActorId, StringComparison.Ordinal) &&
               string.Equals(
                   State.PendingToolApprovalPublisherActorId,
                   pending.WorkflowActorId,
                   StringComparison.Ordinal);
    }

    private static bool SameTerminalIdentity(
        WorkflowRunTerminalNotification left,
        WorkflowRunTerminalNotification right) =>
        string.Equals(left.DeliveryId, right.DeliveryId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowActorId, right.WorkflowActorId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowCommandId, right.WorkflowCommandId, StringComparison.Ordinal);

    private static bool SameToolApprovalIdentity(
        WorkflowRunToolApprovalNotification left,
        WorkflowRunToolApprovalNotification right) =>
        string.Equals(left.DeliveryId, right.DeliveryId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowActorId, right.WorkflowActorId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowCommandId, right.WorkflowCommandId, StringComparison.Ordinal) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.ExecutionId, right.ExecutionId, StringComparison.Ordinal) &&
        string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal) &&
        string.Equals(left.ApprovalRequestId, right.ApprovalRequestId, StringComparison.Ordinal);

    private ConversationReference BuildConversationReference()
    {
        var bot = string.IsNullOrWhiteSpace(State.BotRegistrationId)
            ? "nyx-relay-bot"
            : State.BotRegistrationId;
        var partition = string.IsNullOrWhiteSpace(State.RegistrationScopeId)
            ? State.ReplyMessageId
            : State.RegistrationScopeId;
        return ConversationReference.Create(
            ChannelId.From(State.ChannelPlatform),
            BotInstanceId.From(bot),
            ConversationScope.Unspecified,
            partition,
            string.IsNullOrWhiteSpace(partition) ? State.ReplyMessageId : partition,
            State.ReplyMessageId);
    }

    private static string BuildTerminalReplyText(WorkflowRunTerminalNotification notification)
    {
        var text = ResolveTerminalText(notification);
        return notification.Status switch
        {
            WorkflowRunTerminalStatus.Completed => text,
            WorkflowRunTerminalStatus.Stopped => $"Workflow stopped: {text}",
            _ => $"Workflow failed ({ResolveTerminalErrorCode(notification)}): {text}",
        };
    }

    private static string ResolveTerminalText(WorkflowRunTerminalNotification notification) =>
        notification.Status switch
        {
            WorkflowRunTerminalStatus.Completed => string.IsNullOrWhiteSpace(notification.Output)
                ? "Workflow run completed without a result to display."
                : notification.Output.Trim(),
            WorkflowRunTerminalStatus.Stopped => string.IsNullOrWhiteSpace(notification.Error)
                ? "Workflow stopped."
                : notification.Error.Trim(),
            _ => string.IsNullOrWhiteSpace(notification.Error)
                ? "Workflow failed."
                : notification.Error.Trim(),
        };

    private static string ResolveTerminalErrorCode(WorkflowRunTerminalNotification notification) =>
        notification.Status switch
        {
            WorkflowRunTerminalStatus.Completed => string.Empty,
            WorkflowRunTerminalStatus.Stopped => "workflow_run_stopped",
            _ => "workflow_run_error",
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

    private (string Code, string Summary)? ValidateReservation(WorkflowRunDeliveryReserveRequested command)
    {
        if (string.IsNullOrWhiteSpace(command.DeliveryId))
            return ("delivery_id_required", "Workflow run delivery requires a business delivery id.");
        if (string.IsNullOrWhiteSpace(command.ExpectedWorkflowCommandId))
            return ("workflow_command_id_required", "Workflow run delivery requires an expected workflow command id.");
        if (string.IsNullOrWhiteSpace(command.ChannelPlatform))
            return ("channel_platform_required", "Workflow run delivery requires a channel platform.");
        if (string.IsNullOrWhiteSpace(command.ReplyMessageId))
            return ("reply_message_id_required", "Workflow run delivery requires a reply message id.");
        if (string.IsNullOrWhiteSpace(command.WorkflowResultDeliveryCredential?.SecretReference?.Ref) ||
            string.IsNullOrWhiteSpace(command.WorkflowResultDeliveryCredential.SubjectId))
        {
            return (ChannelWorkflowDeliveryUnavailableCode, ChannelWorkflowDeliveryUnavailableSummary);
        }
        if (command.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
            return (ReservationExpiredCode, "Workflow run delivery reservation has expired.");
        if (command.ExpiresAtUnixMs > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            return ("reservation_expiry_invalid", "Workflow run delivery reservation expiry is invalid.");

        return null;
    }

    private static bool IsTerminal(WorkflowRunDeliveryStatus status) =>
        status is WorkflowRunDeliveryStatus.Delivered or
            WorkflowRunDeliveryStatus.Failed or
            WorkflowRunDeliveryStatus.Abandoned;

    private static WorkflowRunDeliveryGAgentState ApplyReserved(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryReservedEvent evt)
    {
        var next = current.Clone();
        next.DeliveryId = evt.DeliveryId;
        next.ExpectedWorkflowCommandId = evt.ExpectedWorkflowCommandId;
        next.ChannelPlatform = evt.ChannelPlatform;
        next.ReplyMessageId = evt.ReplyMessageId;
        next.PlatformMessageId = evt.PlatformMessageId;
        next.RegistrationScopeId = evt.RegistrationScopeId;
        next.WorkflowResultDeliveryCredential = evt.WorkflowResultDeliveryCredential?.Clone();
        next.BotRegistrationId = evt.BotRegistrationId;
        next.ReservationExpiresAtUnixMs = evt.ExpiresAtUnixMs;
        next.Status = WorkflowRunDeliveryStatus.Reserved;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyStarted(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryStartedEvent evt)
    {
        var next = current.Clone();
        next.WorkflowActorId = evt.WorkflowActorId;
        next.WorkflowRunId = evt.WorkflowRunId;
        next.WorkflowCommandId = evt.WorkflowCommandId;
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId;
        next.StreamTopic = evt.StreamTopic;
        next.Status = WorkflowRunDeliveryStatus.Started;
        next.StartedAtUnixMs = evt.StartedAtUnixMs;
        next.CompletedAtUnixMs = 0;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyTerminalBuffered(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryTerminalNotificationBufferedEvent evt)
    {
        var next = current.Clone();
        next.PendingTerminalNotification = evt.Notification?.Clone();
        next.PendingTerminalPublisherActorId = evt.PublisherActorId;
        next.PendingTerminalBufferedAtUnixMs = evt.BufferedAtUnixMs;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyTerminalDiscarded(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryTerminalNotificationDiscardedEvent _)
    {
        var next = current.Clone();
        next.PendingTerminalNotification = null;
        next.PendingTerminalPublisherActorId = string.Empty;
        next.PendingTerminalBufferedAtUnixMs = 0;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyToolApprovalBuffered(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryToolApprovalNotificationBufferedEvent evt)
    {
        if (evt.Notification is null)
            return current;

        var next = current.Clone();
        next.PendingToolApprovalNotification = evt.Notification.Clone();
        next.PendingToolApprovalPublisherActorId = evt.PublisherActorId;
        next.PendingToolApprovalBufferedAtUnixMs = evt.BufferedAtUnixMs;
        next.ToolApprovalDeliveryAttempt = 0;
        next.ToolApprovalRetryCallbackId = string.Empty;
        next.ToolApprovalRetryAtUnixMs = 0;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyToolApprovalDelivered(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryToolApprovalNotificationDeliveredEvent evt)
    {
        var next = current.Clone();
        if (!MatchesPendingToolApproval(next, evt.DeliveryId, evt.WorkflowCommandId, evt.ApprovalRequestId))
            return next;

        if (!next.DeliveredToolApprovalRequestIds.Contains(evt.ApprovalRequestId, StringComparer.Ordinal))
            next.DeliveredToolApprovalRequestIds.Add(evt.ApprovalRequestId);
        ClearPendingToolApproval(next);
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyToolApprovalRetryScheduled(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryToolApprovalNotificationRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (!MatchesPendingToolApproval(next, evt.DeliveryId, evt.WorkflowCommandId, evt.ApprovalRequestId) ||
            evt.Attempt <= next.ToolApprovalDeliveryAttempt)
        {
            return next;
        }

        next.ToolApprovalDeliveryAttempt = evt.Attempt;
        next.ToolApprovalRetryCallbackId = evt.CallbackId;
        next.ToolApprovalRetryAtUnixMs = evt.RetryAtUnixMs;
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyToolApprovalDiscarded(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryToolApprovalNotificationDiscardedEvent evt)
    {
        var next = current.Clone();
        if (MatchesPendingToolApproval(next, evt.DeliveryId, evt.WorkflowCommandId, evt.ApprovalRequestId))
            ClearPendingToolApproval(next);
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplySucceeded(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliverySucceededEvent evt)
    {
        var next = current.Clone();
        next.Status = WorkflowRunDeliveryStatus.Delivered;
        next.CompletedAtUnixMs = evt.DeliveredAtUnixMs;
        next.DeliveryAttempt = evt.Attempt;
        next.LastAttemptAtUnixMs = evt.DeliveredAtUnixMs;
        next.DeliveredActivityId = evt.SentActivityId;
        next.DeliveredPlatformMessageId = evt.PlatformMessageId;
        next.ErrorCode = string.Empty;
        next.ErrorSummary = string.Empty;
        next.TerminalOutcome = evt.TerminalOutcome;
        next.TerminalText = evt.TerminalText;
        next.TerminalErrorCode = evt.TerminalErrorCode;
        next.TerminalObservedAtUnixMs = evt.TerminalObservedAtUnixMs;
        ClearPendingTerminal(next);
        ClearPendingToolApproval(next);
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyFailed(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryFailedEvent evt)
    {
        var next = current.Clone();
        if (string.IsNullOrWhiteSpace(next.DeliveryId))
            next.DeliveryId = evt.DeliveryId;
        if (string.IsNullOrWhiteSpace(next.ExpectedWorkflowCommandId))
            next.ExpectedWorkflowCommandId = evt.WorkflowCommandId;
        next.Status = WorkflowRunDeliveryStatus.Failed;
        next.CompletedAtUnixMs = evt.FailedAtUnixMs;
        next.DeliveryAttempt = evt.Attempt;
        next.LastAttemptAtUnixMs = evt.FailedAtUnixMs;
        next.ErrorCode = evt.ErrorCode;
        next.ErrorSummary = evt.ErrorSummary;
        next.TerminalOutcome = evt.TerminalOutcome;
        next.TerminalText = evt.TerminalText;
        next.TerminalErrorCode = evt.TerminalErrorCode;
        next.TerminalObservedAtUnixMs = evt.TerminalObservedAtUnixMs;
        ClearPendingTerminal(next);
        ClearPendingToolApproval(next);
        return next;
    }

    private static WorkflowRunDeliveryGAgentState ApplyAbandoned(
        WorkflowRunDeliveryGAgentState current,
        WorkflowRunDeliveryAbandonedEvent evt)
    {
        var next = current.Clone();
        next.Status = WorkflowRunDeliveryStatus.Abandoned;
        next.CompletedAtUnixMs = evt.AbandonedAtUnixMs;
        next.ErrorCode = "workflow_run_delivery_abandoned";
        next.ErrorSummary = evt.Reason;
        ClearPendingTerminal(next);
        ClearPendingToolApproval(next);
        return next;
    }

    private static void ClearPendingTerminal(WorkflowRunDeliveryGAgentState state)
    {
        state.PendingTerminalNotification = null;
        state.PendingTerminalPublisherActorId = string.Empty;
        state.PendingTerminalBufferedAtUnixMs = 0;
    }

    private static bool MatchesPendingToolApproval(
        WorkflowRunDeliveryGAgentState state,
        string? deliveryId,
        string? workflowCommandId,
        string? approvalRequestId) =>
        state.PendingToolApprovalNotification is not null &&
        string.Equals(state.PendingToolApprovalNotification.DeliveryId, deliveryId, StringComparison.Ordinal) &&
        string.Equals(
            state.PendingToolApprovalNotification.WorkflowCommandId,
            workflowCommandId,
            StringComparison.Ordinal) &&
        string.Equals(
            state.PendingToolApprovalNotification.ApprovalRequestId,
            approvalRequestId,
            StringComparison.Ordinal);

    private static void ClearPendingToolApproval(WorkflowRunDeliveryGAgentState state)
    {
        state.PendingToolApprovalNotification = null;
        state.PendingToolApprovalPublisherActorId = string.Empty;
        state.PendingToolApprovalBufferedAtUnixMs = 0;
        state.ToolApprovalDeliveryAttempt = 0;
        state.ToolApprovalRetryCallbackId = string.Empty;
        state.ToolApprovalRetryAtUnixMs = 0;
    }
}
