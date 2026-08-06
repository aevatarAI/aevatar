using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.WorkOrder;

/// <summary>
/// Authority actor for one durable Team execution intent.
/// </summary>
[GAgent("studio.work-order")]
public sealed partial class WorkOrderGAgent : GAgentBase<WorkOrderState>, IProjectedActor
{
    private const int ExecutionRetryInitialDelayMilliseconds = 250;
    private const int ExecutionRetryMaxDelayMilliseconds = 30_000;
    private readonly IWorkOrderExecutionScheduler? _executionScheduler;

    public static string ProjectionKind => "work-order";

    public WorkOrderGAgent(IWorkOrderExecutionScheduler? executionScheduler = null)
    {
        _executionScheduler = executionScheduler;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (string.IsNullOrWhiteSpace(State.WorkOrderId) || IsTerminal(State.LifecycleStatus))
            return;

        if (await EnsureTimeoutScheduledAsync(ct))
            return;

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending &&
            string.IsNullOrWhiteSpace(State.Run?.RunId))
        {
            await ScheduleExecutionAndWatchdogAsync(ct);
        }
    }

    [EventHandler(EndpointName = "createWorkOrder")]
    public async Task HandleCreateAsync(CreateWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCreate(command);

        if (!string.IsNullOrWhiteSpace(State.WorkOrderId))
        {
            EnsureSameCreate(State, command);
            await EnsureTimeoutScheduledAsync();
            return;
        }

        var now = command.RequestedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        await PersistDomainEventsAsync(
        [
            new WorkOrderCreatedEvent
            {
                Request = command.Clone(),
                CreatedAtUtc = now,
            },
            new WorkOrderReadyEvent
            {
                ReadyAtUtc = now,
            },
        ]);

        await EnsureTimeoutScheduledAsync();
    }

    [EventHandler(EndpointName = "reassignWorkOrder")]
    public async Task HandleReassignAsync(ReassignWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureInitialized(command.WorkOrderId);
        EnsureRequester(command.RequestedBy);

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
        if (State.LifecycleStatus is not (
                WorkOrderLifecycleStatus.Accepted or
                WorkOrderLifecycleStatus.Ready))
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' cannot be reassigned from '{State.LifecycleStatus}'.");
        }

        if (IsSameAssignment(State, command))
            return;

        EnsureRequired(command.MemberId, nameof(command.MemberId));
        EnsureRequired(command.PublishedServiceId, nameof(command.PublishedServiceId));
        EnsureRequired(command.ServiceRevisionId, nameof(command.ServiceRevisionId));
        EnsureRequired(command.ImplementationKind, nameof(command.ImplementationKind));

        await PersistDomainEventAsync(new WorkOrderReassignedEvent
        {
            MemberId = command.MemberId.Trim(),
            PublishedServiceId = command.PublishedServiceId.Trim(),
            WorkflowId = command.WorkflowId?.Trim() ?? string.Empty,
            ServiceRevisionId = command.ServiceRevisionId.Trim(),
            ImplementationKind = command.ImplementationKind.Trim(),
            ReassignedBy = command.RequestedBy.Clone(),
            ReassignedAtUtc = command.RequestedAtUtc
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    [EventHandler(EndpointName = "dispatchWorkOrder")]
    public async Task HandleDispatchAsync(DispatchWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureInitialized(command.WorkOrderId);
        EnsureRequester(command.RequestedBy);
        EnsureCanonicalIdentity(
            "dispatch_command_id",
            command.DispatchCommandId,
            WorkOrderConventions.BuildDispatchCommandId(State.WorkOrderId));
        EnsureCanonicalIdentity(
            "requested_run_id",
            command.RequestedRunId,
            WorkOrderConventions.BuildRequestedRunId(State.WorkOrderId));
        EnsureCanonicalIdentity(
            "terminal_delivery_id",
            command.TerminalDeliveryId,
            WorkOrderConventions.BuildTerminalDeliveryId(State.WorkOrderId));

        var sameDispatch =
            string.Equals(State.DispatchCommandId, command.DispatchCommandId, StringComparison.Ordinal) &&
            string.Equals(State.RequestedRunId, command.RequestedRunId, StringComparison.Ordinal) &&
            string.Equals(State.TerminalDeliveryId, command.TerminalDeliveryId, StringComparison.Ordinal);

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
        if (sameDispatch && State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending)
        {
            await SendExecutionRequestAsync();
            return;
        }
        if (sameDispatch && (State.LifecycleStatus == WorkOrderLifecycleStatus.Running || IsTerminal(State.LifecycleStatus)))
            return;

        if (State.LifecycleStatus != WorkOrderLifecycleStatus.Ready)
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' cannot dispatch from '{State.LifecycleStatus}'.");
        }

        await PersistDomainEventAsync(new WorkOrderDispatchRequestedEvent
        {
            DispatchCommandId = command.DispatchCommandId.Trim(),
            RequestedRunId = command.RequestedRunId.Trim(),
            TerminalDeliveryId = command.TerminalDeliveryId.Trim(),
            RequestedBy = command.RequestedBy.Clone(),
            RequestedAtUtc = command.RequestedAtUtc
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await SendExecutionRequestAsync();
    }

    [EventHandler(EndpointName = "executeWorkOrder", AllowSelfHandling = true)]
    public async Task HandleExecuteAsync(ExecuteWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending ||
            !string.Equals(State.WorkOrderId, command.WorkOrderId, StringComparison.Ordinal) ||
            !string.Equals(State.DispatchCommandId, command.DispatchCommandId, StringComparison.Ordinal))
        {
            return;
        }

        if (State.Run != null && !string.IsNullOrWhiteSpace(State.Run.RunId))
            return;

        EnsureInboundPublisherMatches(Id, "WorkOrder execute signal");
        await ScheduleExecutionAndWatchdogAsync();
    }

    [EventHandler(EndpointName = "workOrderExecutionAccepted")]
    public async Task HandleExecutionAcceptedAsync(WorkOrderExecutionAcceptedContinuation continuation)
    {
        if (!MatchesPendingExecution(
                continuation.WorkOrderId,
                continuation.DispatchCommandId,
                continuation.RequestedRunId))
            return;

        EnsureInboundPublisherMatches(
            WorkOrderConventions.ExecutionWorkerPublisherActorId,
            "WorkOrder execution continuation");
        ValidateAcceptedExecution(continuation.Accepted);
        await PersistDomainEventAsync(new WorkOrderRunAcceptedEvent
        {
            Accepted = continuation.Accepted.Clone(),
        });
    }

    [EventHandler(EndpointName = "workOrderExecutionFailed")]
    public async Task HandleExecutionFailedAsync(WorkOrderExecutionFailedContinuation continuation)
    {
        if (!MatchesPendingExecution(
                continuation.WorkOrderId,
                continuation.DispatchCommandId,
                continuation.RequestedRunId))
            return;

        EnsureInboundPublisherMatches(
            WorkOrderConventions.ExecutionWorkerPublisherActorId,
            "WorkOrder execution continuation");
        await PersistDomainEventAsync(new WorkOrderDispatchFailedEvent
        {
            Failure = continuation.Failed?.Failure?.Clone() ?? new WorkOrderFailureReference
            {
                Code = "WORK_ORDER_DISPATCH_FAILED",
                Message = "WorkOrder execution failed without a typed failure.",
                Source = "work-order-execution-worker",
                ReferenceId = continuation.DispatchCommandId,
            },
            FailedAtUtc = continuation.Failed?.FailedAtUtc?.Clone()
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    [EventHandler(EndpointName = "retryWorkOrderExecution", AllowSelfHandling = true)]
    public async Task HandleExecutionRetryFiredAsync(WorkOrderExecutionRetryFired evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!MatchesPendingExecution(
                evt.WorkOrderId,
                evt.DispatchCommandId,
            evt.RequestedRunId) ||
            evt.Attempt <= 0 ||
            evt.Attempt != State.ExecutionRetryAttempt ||
            !string.IsNullOrWhiteSpace(State.Run?.RunId))
        {
            return;
        }

        EnsureInboundPublisherMatches(Id, "WorkOrder execution retry signal");
        await ScheduleExecutionAndWatchdogAsync();
    }

    [EventHandler(EndpointName = "cancelWorkOrder")]
    public async Task HandleCancelAsync(CancelWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureInitialized(command.WorkOrderId);
        EnsureRequester(command.RequestedBy);

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
        if (State.LifecycleStatus == WorkOrderLifecycleStatus.Cancelled)
            return;

        if (State.LifecycleStatus is not (
                WorkOrderLifecycleStatus.Accepted or
                WorkOrderLifecycleStatus.Ready))
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' cannot be cancelled after dispatch authorization.");
        }

        await PersistDomainEventAsync(new WorkOrderCancelledEvent
        {
            CancelledBy = command.RequestedBy.Clone(),
            Reason = command.Reason?.Trim() ?? string.Empty,
            CancelledAtUtc = command.RequestedAtUtc
                ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    [EventHandler(EndpointName = "timeoutWorkOrder", AllowSelfHandling = true)]
    public async Task HandleTimeoutAsync(WorkOrderTimeoutFired evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (IsTerminal(State.LifecycleStatus) || State.TimeoutAtUtc == null)
            return;
        if (!string.Equals(State.WorkOrderId, evt.WorkOrderId, StringComparison.Ordinal) ||
            !Equals(State.TimeoutAtUtc, evt.TimeoutAtUtc))
        {
            return;
        }

        EnsureInboundPublisherMatches(Id, "WorkOrder timeout signal");
        var now = DateTimeOffset.UtcNow;
        var timeoutAt = State.TimeoutAtUtc.ToDateTimeOffset();
        if (now < timeoutAt)
        {
            await EnsureTimeoutScheduledAsync();
            return;
        }

        await PersistDomainEventAsync(new WorkOrderTimedOutEvent
        {
            Reason = "WorkOrder deadline elapsed; the linked Run, if any, was not claimed cancelled.",
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(now),
        });
    }

    [EventHandler(EndpointName = "recordWorkflowTerminal")]
    public Task HandleWorkflowTerminalAsync(WorkflowRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return RecordRunOutcomeAsync(new WorkOrderRunOutcomeReference
        {
            DeliveryId = notification.DeliveryId,
            RunId = notification.WorkflowRunId,
            RunActorId = notification.WorkflowActorId,
            CommandId = notification.WorkflowCommandId,
            CorrelationId = notification.WorkflowCorrelationId,
            Outcome = notification.Status switch
            {
                WorkflowRunTerminalStatus.Completed => WorkOrderTerminalOutcome.Succeeded,
                WorkflowRunTerminalStatus.Failed => WorkOrderTerminalOutcome.Failed,
                WorkflowRunTerminalStatus.Stopped => WorkOrderTerminalOutcome.Stopped,
                _ => WorkOrderTerminalOutcome.Unspecified,
            },
            TerminalAtUtc = notification.TerminalAt?.Clone(),
        }, notification.WorkflowActorId);
    }

    [EventHandler(EndpointName = "recordWorkflowStarted")]
    public async Task HandleWorkflowStartedAsync(WorkflowRunStartedNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        EnsureAcceptedRunIdentity(
            notification.DeliveryId,
            notification.WorkflowRunId,
            notification.WorkflowActorId,
            notification.WorkflowCommandId,
            notification.WorkflowCorrelationId);
        EnsureInboundPublisherMatches(
            notification.WorkflowActorId,
            "Workflow Run started evidence");
        if (notification.StartedAt == null)
            throw new InvalidOperationException("workflow Run started notification requires started_at.");

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.Running || IsTerminal(State.LifecycleStatus))
            return;

        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending)
        {
            throw new InvalidOperationException(
                $"workflow Run started evidence cannot advance work order from '{State.LifecycleStatus}'.");
        }

        await PersistDomainEventAsync(new WorkOrderRunStartedEvent
        {
            DeliveryId = notification.DeliveryId,
            RunId = notification.WorkflowRunId,
            RunActorId = notification.WorkflowActorId,
            CommandId = notification.WorkflowCommandId,
            CorrelationId = notification.WorkflowCorrelationId,
            StartedAtUtc = notification.StartedAt.Clone(),
        });
    }

    [EventHandler(EndpointName = "recordServiceRunTerminal")]
    public Task HandleServiceRunTerminalAsync(ServiceRunTerminalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return RecordRunOutcomeAsync(new WorkOrderRunOutcomeReference
        {
            DeliveryId = notification.DeliveryId,
            RunId = notification.RunId,
            RunActorId = notification.TargetActorId,
            CommandId = notification.CommandId,
            CorrelationId = notification.CorrelationId,
            Outcome = notification.Status switch
            {
                ServiceRunStatus.Completed => WorkOrderTerminalOutcome.Succeeded,
                ServiceRunStatus.Failed => WorkOrderTerminalOutcome.Failed,
                ServiceRunStatus.Stopped => WorkOrderTerminalOutcome.Stopped,
                _ => WorkOrderTerminalOutcome.Unspecified,
            },
            TerminalAtUtc = notification.TerminalAt?.Clone(),
        }, BuildExpectedServiceRunPublisherActorId());
    }

    private async Task RecordRunOutcomeAsync(
        WorkOrderRunOutcomeReference outcome,
        string expectedPublisherActorId)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Outcome == WorkOrderTerminalOutcome.Unspecified)
            throw new InvalidOperationException("Run outcome is required.");
        if (outcome.TerminalAtUtc == null)
            throw new InvalidOperationException("Run outcome terminal_at_utc is required.");
        EnsureAcceptedRunIdentity(
            outcome.DeliveryId,
            outcome.RunId,
            outcome.RunActorId,
            outcome.CommandId,
            outcome.CorrelationId);
        EnsureInboundPublisherMatches(expectedPublisherActorId, "Run outcome");

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.TimedOut)
        {
            if (RunOutcomesEqual(State.LateRunOutcome, outcome))
                return;
            if (State.LateRunOutcome != null)
                throw new InvalidOperationException("conflicting late Run outcome was received.");

            await PersistDomainEventAsync(new WorkOrderLateRunOutcomeObservedEvent
            {
                Outcome = outcome.Clone(),
            });
            return;
        }

        if (IsTerminal(State.LifecycleStatus))
        {
            if (RunOutcomesEqual(State.RunOutcome, outcome))
                return;
            throw new InvalidOperationException("conflicting Run outcome was received.");
        }

        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending &&
            State.LifecycleStatus != WorkOrderLifecycleStatus.Running)
        {
            throw new InvalidOperationException(
                $"Run outcome cannot advance work order from '{State.LifecycleStatus}'.");
        }

        await PersistDomainEventAsync(new WorkOrderRunOutcomeObservedEvent
        {
            LifecycleStatus = outcome.Outcome switch
            {
                WorkOrderTerminalOutcome.Succeeded => WorkOrderLifecycleStatus.Completed,
                WorkOrderTerminalOutcome.Failed => WorkOrderLifecycleStatus.Failed,
                WorkOrderTerminalOutcome.Stopped => WorkOrderLifecycleStatus.Stopped,
                _ => throw new InvalidOperationException("terminal outcome is unsupported."),
            },
            Outcome = outcome.Clone(),
        });
    }

    private WorkOrderExecutionRequest BuildExecutionRequest() =>
        new()
        {
            WorkOrderActorId = Id,
            WorkOrderId = State.WorkOrderId,
            ScopeId = State.ScopeId,
            TeamId = State.TeamId,
            MemberId = State.MemberId,
            PublishedServiceId = State.PublishedServiceId,
            WorkflowId = State.WorkflowId,
            ServiceRevisionId = State.ServiceRevisionId,
            ImplementationKind = State.ImplementationKind,
            EndpointId = State.EndpointId,
            Input = State.Input?.Clone(),
            DispatchCommandId = State.DispatchCommandId,
            RequestedRunId = State.RequestedRunId,
            TerminalDeliveryId = State.TerminalDeliveryId,
            DeadlineAtUtc = State.TimeoutAtUtc?.Clone(),
        };

    private async Task ScheduleExecutionAndWatchdogAsync(CancellationToken ct = default)
    {
        if (_executionScheduler == null)
        {
            await ScheduleExecutionRetryAsync(ct);
            return;
        }

        try
        {
            var admission = _executionScheduler.ScheduleAsync(BuildExecutionRequest().Clone(), ct);
            if (!admission.IsCompleted)
            {
                throw new InvalidOperationException(
                    "WorkOrder execution scheduler admission must complete without blocking the actor turn.");
            }

            await admission;
        }
        catch (WorkOrderExecutionQueueFullException)
        {
            await ScheduleExecutionRetryAsync(ct);
            return;
        }

        await ScheduleExecutionRetryAsync(ct);
    }

    private async Task ScheduleExecutionRetryAsync(CancellationToken ct)
    {
        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending ||
            !string.IsNullOrWhiteSpace(State.Run?.RunId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var attempt = checked(State.ExecutionRetryAttempt + 1);
        var exponent = Math.Min(attempt - 1, 30);
        var exponentialDelay = ExecutionRetryInitialDelayMilliseconds * Math.Pow(2, exponent);
        var delayMilliseconds = Math.Min(ExecutionRetryMaxDelayMilliseconds, exponentialDelay);
        var backoff = TimeSpan.FromMilliseconds(delayMilliseconds);
        var due = backoff;
        if (State.TimeoutAtUtc != null)
        {
            var remaining = State.TimeoutAtUtc.ToDateTimeOffset() - now;
            if (remaining <= TimeSpan.Zero)
            {
                await SendToAsync(
                    Id,
                    new WorkOrderTimeoutFired
                    {
                        WorkOrderId = State.WorkOrderId,
                        TimeoutAtUtc = State.TimeoutAtUtc.Clone(),
                    },
                    ct);
                return;
            }

            due = remaining < backoff ? remaining : backoff;
        }
        var retryAt = Timestamp.FromDateTimeOffset(now.Add(due));
        var callbackId = BuildExecutionRetryCallbackId(
            State.WorkOrderId,
            State.DispatchCommandId,
            attempt);
        var fired = new WorkOrderExecutionRetryFired
        {
            WorkOrderId = State.WorkOrderId,
            DispatchCommandId = State.DispatchCommandId,
            RequestedRunId = State.RequestedRunId,
            Attempt = attempt,
        };

        await ScheduleSelfDurableTimeoutAsync(callbackId, due, fired, ct: ct);
        await PersistDomainEventAsync(new WorkOrderExecutionRetryScheduledEvent
        {
            WorkOrderId = fired.WorkOrderId,
            DispatchCommandId = fired.DispatchCommandId,
            RequestedRunId = fired.RequestedRunId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAtUtc = retryAt,
        }, ct);
    }

    private Task SendExecutionRequestAsync(CancellationToken ct = default) =>
        SendToAsync(
            Id,
            new ExecuteWorkOrder
            {
                WorkOrderId = State.WorkOrderId,
                DispatchCommandId = State.DispatchCommandId,
            },
            ct);

    private async Task<bool> EnsureTimeoutScheduledAsync(CancellationToken ct = default)
    {
        if (State.TimeoutAtUtc == null || IsTerminal(State.LifecycleStatus))
            return false;

        var timeoutAt = State.TimeoutAtUtc.ToDateTimeOffset();
        var due = timeoutAt - DateTimeOffset.UtcNow;
        var timeoutEvent = new WorkOrderTimeoutFired
        {
            WorkOrderId = State.WorkOrderId,
            TimeoutAtUtc = State.TimeoutAtUtc.Clone(),
        };
        if (due <= TimeSpan.Zero)
        {
            await SendToAsync(Id, timeoutEvent, ct);
            return true;
        }

        await ScheduleSelfDurableTimeoutAsync(
            BuildTimeoutCallbackId(State.WorkOrderId, State.TimeoutAtUtc),
            due,
            timeoutEvent,
            ct: ct);
        return false;
    }

    private void ValidateAcceptedExecution(WorkOrderExecutionAccepted accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (!string.Equals(accepted.RunId, State.RequestedRunId, StringComparison.Ordinal) ||
            !string.Equals(accepted.CommandId, State.DispatchCommandId, StringComparison.Ordinal) ||
            !string.Equals(accepted.CorrelationId, State.DispatchCommandId, StringComparison.Ordinal) ||
            !string.Equals(accepted.RevisionId, State.ServiceRevisionId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(accepted.RunActorId) ||
            string.IsNullOrWhiteSpace(accepted.DeploymentId) ||
            accepted.AcceptedAtUtc == null)
        {
            throw new InvalidOperationException(
                "WorkOrder execution receipt does not match the authorized Run link.");
        }
    }

    private bool MatchesPendingExecution(string workOrderId, string dispatchCommandId, string requestedRunId) =>
        State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending &&
        string.IsNullOrWhiteSpace(State.Run?.RunId) &&
        string.Equals(State.WorkOrderId, workOrderId, StringComparison.Ordinal) &&
        string.Equals(State.DispatchCommandId, dispatchCommandId, StringComparison.Ordinal) &&
        string.Equals(State.RequestedRunId, requestedRunId, StringComparison.Ordinal);

    private void EnsureAcceptedRunIdentity(
        string deliveryId,
        string runId,
        string runActorId,
        string commandId,
        string correlationId)
    {
        if (State.Run == null || string.IsNullOrWhiteSpace(State.Run.RunId))
            throw new InvalidOperationException("Run evidence arrived before Run acceptance was recorded.");
        if (!string.Equals(State.TerminalDeliveryId, deliveryId, StringComparison.Ordinal) ||
            !string.Equals(State.Run.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(State.Run.RunActorId, runActorId, StringComparison.Ordinal) ||
            !string.Equals(State.Run.CommandId, commandId, StringComparison.Ordinal) ||
            !string.Equals(State.Run.CorrelationId, correlationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run evidence does not match work order '{State.WorkOrderId}' Run identity.");
        }
    }

    private void EnsureInboundPublisherMatches(string expectedPublisherActorId, string evidenceName)
    {
        if (ActiveInboundEnvelope == null)
            return;

        var publisherActorId = ActiveInboundEnvelope.Route?.PublisherActorId?.Trim() ?? string.Empty;
        if (!string.Equals(publisherActorId, expectedPublisherActorId?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{evidenceName} envelope publisher '{publisherActorId}' does not match expected actor '{expectedPublisherActorId}'.");
        }
    }

    private string BuildExpectedServiceRunPublisherActorId()
    {
        var runId = State.Run?.RunId;
        if (string.IsNullOrWhiteSpace(runId))
            return string.Empty;

        return ServiceRunIds.BuildActorId(State.ScopeId, State.PublishedServiceId, runId);
    }

    private void EnsureInitialized(string workOrderId)
    {
        if (string.IsNullOrWhiteSpace(State.WorkOrderId))
            throw new InvalidOperationException("work order not yet created.");
        if (!string.Equals(State.WorkOrderId, workOrderId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"work order actor '{Id}' owns '{State.WorkOrderId}', not '{workOrderId}'.");
    }

    private void EnsureExpectedVersion(long expectedLifecycleVersion)
    {
        if (State.LifecycleVersion != expectedLifecycleVersion)
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' lifecycle version is {State.LifecycleVersion}, not {expectedLifecycleVersion}.");
        }
    }

    private void EnsureRequester(WorkOrderPrincipal principal)
    {
        if (!PrincipalsEqual(State.Requester, principal))
            throw new InvalidOperationException("work order command principal is not the requester.");
    }

    private void ValidateCreate(CreateWorkOrder command)
    {
        if (command.ExpectedLifecycleVersion != 0)
            throw new InvalidOperationException("new work order expected_lifecycle_version must be zero.");
        EnsureRequired(command.WorkOrderId, nameof(command.WorkOrderId));
        EnsureRequired(command.DedupKey, nameof(command.DedupKey));
        EnsureRequired(command.ScopeId, nameof(command.ScopeId));
        EnsureRequired(command.TeamId, nameof(command.TeamId));
        EnsureRequired(command.MemberId, nameof(command.MemberId));
        EnsureRequired(command.PublishedServiceId, nameof(command.PublishedServiceId));
        EnsureRequired(command.ServiceRevisionId, nameof(command.ServiceRevisionId));
        EnsureRequired(command.ImplementationKind, nameof(command.ImplementationKind));
        EnsureRequired(command.EndpointId, nameof(command.EndpointId));
        EnsureRequired(command.Intent, nameof(command.Intent));
        EnsureRequired(command.Requester?.PrincipalId, "requester.principal_id");
        EnsureRequired(command.Requester?.PrincipalKind, "requester.principal_kind");
        if (command.Input?.Chat == null)
            throw new InvalidOperationException("work order chat input is required.");
        var requestedAt = command.RequestedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        if (command.TimeoutAtUtc != null && command.TimeoutAtUtc.ToDateTimeOffset() <= requestedAt)
        {
            throw new InvalidOperationException(
                "timeout_at_utc must be later than requested_at_utc.");
        }

        var canonicalWorkOrderId = WorkOrderConventions.BuildWorkOrderId(command.ScopeId, command.DedupKey);
        if (!string.Equals(command.WorkOrderId.Trim(), canonicalWorkOrderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "work_order_id must be the canonical identity for scope_id and dedup_key.");
        }

        var canonicalActorId = WorkOrderConventions.BuildActorId(command.ScopeId, canonicalWorkOrderId);
        if (!string.Equals(Id, canonicalActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"work order actor '{Id}' does not match canonical identity '{canonicalActorId}'.");
        }

    }

    private static void EnsureCanonicalIdentity(string fieldName, string? actual, string expected)
    {
        EnsureRequired(actual, fieldName);
        if (!string.Equals(actual!.Trim(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{fieldName} must use canonical identity '{expected}'.");
    }

    private static void EnsureSameCreate(WorkOrderState state, CreateWorkOrder command)
    {
        if (state.CreationRequest == null ||
            !CreateRequestsLogicallyEqual(state.CreationRequest, command))
        {
            throw new InvalidOperationException("work order logical identity already exists with a different request.");
        }
    }

    private static bool CreateRequestsLogicallyEqual(CreateWorkOrder left, CreateWorkOrder right)
    {
        var normalizedLeft = left.Clone();
        var normalizedRight = right.Clone();
        normalizedLeft.RequestedAtUtc = null;
        normalizedRight.RequestedAtUtc = null;
        return normalizedLeft.Equals(normalizedRight);
    }

    private static bool IsSameAssignment(WorkOrderState state, ReassignWorkOrder command) =>
        string.Equals(state.MemberId, command.MemberId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.PublishedServiceId, command.PublishedServiceId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.WorkflowId, command.WorkflowId?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(state.ServiceRevisionId, command.ServiceRevisionId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(state.ImplementationKind, command.ImplementationKind?.Trim(), StringComparison.Ordinal);

    private static bool PrincipalsEqual(WorkOrderPrincipal? left, WorkOrderPrincipal? right) =>
        left != null && right != null &&
        string.Equals(left.PrincipalId, right.PrincipalId, StringComparison.Ordinal) &&
        string.Equals(left.PrincipalKind, right.PrincipalKind, StringComparison.Ordinal);

    private static bool RunOutcomesEqual(WorkOrderRunOutcomeReference? left, WorkOrderRunOutcomeReference right) =>
        left != null && left.Equals(right);

    private static bool IsTerminal(WorkOrderLifecycleStatus status) =>
        status is WorkOrderLifecycleStatus.Completed or
            WorkOrderLifecycleStatus.Failed or
            WorkOrderLifecycleStatus.Stopped or
            WorkOrderLifecycleStatus.Cancelled or
            WorkOrderLifecycleStatus.TimedOut;

    private static string BuildTimeoutCallbackId(string workOrderId, Timestamp timeoutAt) =>
        $"work-order-timeout-{workOrderId}-{timeoutAt.Seconds}-{timeoutAt.Nanos}";

    private static string BuildExecutionRetryCallbackId(
        string workOrderId,
        string dispatchCommandId,
        int attempt) =>
        $"work-order-execution-retry-{workOrderId}-{dispatchCommandId}-{attempt}";

    private static void EnsureRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
    }

}
