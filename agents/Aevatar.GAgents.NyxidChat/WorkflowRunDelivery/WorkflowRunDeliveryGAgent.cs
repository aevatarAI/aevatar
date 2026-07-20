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
    private static readonly TimeSpan MaxDurableTimeoutDelay =
        TimeSpan.FromMilliseconds(int.MaxValue - 1_000L);
    private readonly NyxIdRelayOutboundPort _outboundPort;
    private readonly IWorkflowResultDeliveryCredentialResolver _credentialResolver;
    private readonly IActorRuntimeCallbackScheduler _callbackScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowRunDeliveryGAgent> _logger;

    public WorkflowRunDeliveryGAgent(
        NyxIdRelayOutboundPort outboundPort,
        IWorkflowResultDeliveryCredentialResolver credentialResolver,
        IActorRuntimeCallbackScheduler callbackScheduler,
        ILogger<WorkflowRunDeliveryGAgent> logger,
        TimeProvider? timeProvider = null)
    {
        _outboundPort = outboundPort ?? throw new ArgumentNullException(nameof(outboundPort));
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

        if (State.PendingTerminalNotification is null &&
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
            if (IsSameReservation(command) && State.PendingTerminalNotification is null)
                await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
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

        if (State.PendingTerminalNotification is not null)
        {
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
            await TryDeliverPendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
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

        if (State.PendingTerminalNotification is not null)
            await TryDeliverPendingTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        else
            await EnsureReservationExpiryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleTerminalNotificationAsync(WorkflowRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (IsTerminal(State.Status))
            return;

        var publisherActorId = ActiveInboundEnvelope?.Route?.PublisherActorId?.Trim() ?? string.Empty;
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

        await PersistDomainEventAsync(new WorkflowRunDeliveryTerminalNotificationBufferedEvent
        {
            DeliveryId = notification.DeliveryId,
            WorkflowCommandId = notification.WorkflowCommandId,
            PublisherActorId = publisherActorId,
            Notification = notification.Clone(),
            BufferedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        });

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
        var deliveryAgentKey = await ResolveWorkflowResultDeliveryAgentKeyAsync(notification, attempt, observedAt, ct)
            .ConfigureAwait(false);
        if (deliveryAgentKey is null)
            return;

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

        if (result.Success)
        {
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
            await PurgeDurableCallbacksBestEffortAsync().ConfigureAwait(false);
            return;
        }

        await PersistDeliveryFailureAsync(
                notification,
                attempt,
                observedAt,
                string.IsNullOrWhiteSpace(result.ErrorCode) ? "workflow_run_delivery_failed" : result.ErrorCode,
                string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Workflow terminal reply delivery failed."
                    : result.ErrorMessage)
            .ConfigureAwait(false);
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

    private async Task ReconcilePendingTerminalAsync(CancellationToken ct)
    {
        if (State.Status == WorkflowRunDeliveryStatus.Reserved)
            await PromoteReservedTerminalAsync().ConfigureAwait(false);
        if (State.Status == WorkflowRunDeliveryStatus.Started)
            await TryDeliverPendingTerminalAsync(ct).ConfigureAwait(false);
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

    private async Task EnsureReservationExpiryAsync(CancellationToken ct)
    {
        if (State.PendingTerminalNotification is not null ||
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

    private static bool SameTerminalIdentity(
        WorkflowRunTerminalNotification left,
        WorkflowRunTerminalNotification right) =>
        string.Equals(left.DeliveryId, right.DeliveryId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowActorId, right.WorkflowActorId, StringComparison.Ordinal) &&
        string.Equals(left.WorkflowCommandId, right.WorkflowCommandId, StringComparison.Ordinal);

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
                ? "Workflow completed."
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
        return next;
    }

    private static void ClearPendingTerminal(WorkflowRunDeliveryGAgentState state)
    {
        state.PendingTerminalNotification = null;
        state.PendingTerminalPublisherActorId = string.Empty;
        state.PendingTerminalBufferedAtUnixMs = 0;
    }
}
