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
    private readonly IWorkOrderExecutionPort? _executionPort;

    public static string ProjectionKind => "work-order";

    public WorkOrderGAgent(IWorkOrderExecutionPort? executionPort = null)
    {
        _executionPort = executionPort;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (string.IsNullOrWhiteSpace(State.WorkOrderId) || IsTerminal(State.LifecycleStatus))
            return;

        if (await EnsureTimeoutScheduledAsync(ct))
            return;

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending)
            await SendExecutionRequestAsync(ct);
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
        var approvalRequired = RequiresApproval(command.PermissionPlan);
        var approval = new WorkOrderApprovalState
        {
            ApprovalId = approvalRequired ? command.ApprovalId : string.Empty,
            Status = approvalRequired
                ? WorkOrderApprovalStatus.Pending
                : WorkOrderApprovalStatus.NotRequired,
        };

        await PersistDomainEventsAsync(
        [
            new WorkOrderCreatedEvent
            {
                Request = command.Clone(),
                CreatedAtUtc = now,
            },
            new WorkOrderPlannedEvent
            {
                LifecycleStatus = approvalRequired
                    ? WorkOrderLifecycleStatus.WaitingApproval
                    : WorkOrderLifecycleStatus.Ready,
                Approval = approval,
                PlannedAtUtc = now,
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

        if (IsSameAssignment(State, command))
            return;

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
        if (State.LifecycleStatus is not (
                WorkOrderLifecycleStatus.Accepted or
                WorkOrderLifecycleStatus.WaitingApproval or
                WorkOrderLifecycleStatus.Ready))
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' cannot be reassigned from '{State.LifecycleStatus}'.");
        }

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

    [EventHandler(EndpointName = "approveWorkOrder")]
    public Task HandleApproveAsync(ApproveWorkOrder command) =>
        HandleApprovalDecisionAsync(
            command.WorkOrderId,
            command.ExpectedLifecycleVersion,
            command.DecisionId,
            command.DecidedBy,
            command.Reason,
            command.DecidedAtUtc,
            WorkOrderApprovalStatus.Approved);

    [EventHandler(EndpointName = "denyWorkOrder")]
    public Task HandleDenyAsync(DenyWorkOrder command) =>
        HandleApprovalDecisionAsync(
            command.WorkOrderId,
            command.ExpectedLifecycleVersion,
            command.DecisionId,
            command.DecidedBy,
            command.Reason,
            command.DecidedAtUtc,
            WorkOrderApprovalStatus.Denied);

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
        if (sameDispatch && State.LifecycleStatus == WorkOrderLifecycleStatus.DispatchPending)
        {
            await SendExecutionRequestAsync();
            return;
        }
        if (sameDispatch && (State.LifecycleStatus == WorkOrderLifecycleStatus.Running || IsTerminal(State.LifecycleStatus)))
            return;

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
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

        if (State.Execution != null && !string.IsNullOrWhiteSpace(State.Execution.RunId))
            return;

        if (_executionPort == null)
        {
            await PersistDispatchFailureAsync(
                "WORK_ORDER_EXECUTION_PORT_UNAVAILABLE",
                "WorkOrder execution port is not registered.",
                "work-order");
            return;
        }

        var result = await _executionPort.ExecuteAsync(BuildExecutionRequest());
        switch (result.ResultCase)
        {
            case WorkOrderExecutionResult.ResultOneofCase.Accepted:
                ValidateAcceptedExecution(result.Accepted);
                await PersistDomainEventAsync(new WorkOrderRunAcceptedEvent
                {
                    Accepted = result.Accepted.Clone(),
                });
                break;
            case WorkOrderExecutionResult.ResultOneofCase.Failed:
                await PersistDomainEventAsync(new WorkOrderDispatchFailedEvent
                {
                    Failure = result.Failed.Failure?.Clone() ?? new WorkOrderFailureReference
                    {
                        Code = "WORK_ORDER_DISPATCH_FAILED",
                        Message = "WorkOrder execution failed without a failure reference.",
                        Source = "work-order-execution-port",
                    },
                    FailedAtUtc = result.Failed.FailedAtUtc
                        ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                });
                break;
            default:
                throw new InvalidOperationException(
                    "WorkOrder execution port returned neither accepted Run evidence nor a typed failure.");
        }
    }

    [EventHandler(EndpointName = "cancelWorkOrder")]
    public async Task HandleCancelAsync(CancelWorkOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureInitialized(command.WorkOrderId);
        EnsureRequester(command.RequestedBy);

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.Cancelled)
            return;

        EnsureExpectedVersion(command.ExpectedLifecycleVersion);
        if (State.LifecycleStatus is not (
                WorkOrderLifecycleStatus.Accepted or
                WorkOrderLifecycleStatus.WaitingApproval or
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
        return RecordTerminalEvidenceAsync(new WorkOrderTerminalEvidence
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
            Output = notification.Output,
            Error = notification.Error,
            TerminalAtUtc = notification.TerminalAt?.Clone(),
            ResultArtifacts = { CloneDeclaredResultArtifacts() },
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

        if (State.Execution!.StartedAtUtc != null)
        {
            if (State.Execution.StartedAtUtc.Equals(notification.StartedAt))
                return;

            throw new InvalidOperationException("conflicting workflow Run started evidence was received.");
        }

        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending &&
            State.LifecycleStatus != WorkOrderLifecycleStatus.Running &&
            !IsTerminal(State.LifecycleStatus))
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
        return RecordTerminalEvidenceAsync(new WorkOrderTerminalEvidence
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
            Output = notification.Output,
            Error = notification.Error,
            TerminalAtUtc = notification.TerminalAt?.Clone(),
            ResultArtifacts = { CloneDeclaredResultArtifacts() },
        }, BuildExpectedServiceRunPublisherActorId());
    }

    private async Task HandleApprovalDecisionAsync(
        string workOrderId,
        long expectedLifecycleVersion,
        string decisionId,
        WorkOrderPrincipal decidedBy,
        string reason,
        Timestamp? decidedAtUtc,
        WorkOrderApprovalStatus decision)
    {
        EnsureInitialized(workOrderId);
        EnsureRequired(decisionId, nameof(decisionId));
        EnsureApprover(decidedBy);

        if (State.Approval != null &&
            State.Approval.Status == decision &&
            string.Equals(State.Approval.DecisionId, decisionId, StringComparison.Ordinal) &&
            PrincipalsEqual(State.Approval.DecidedBy, decidedBy))
        {
            return;
        }

        EnsureExpectedVersion(expectedLifecycleVersion);
        if (State.LifecycleStatus != WorkOrderLifecycleStatus.WaitingApproval ||
            State.Approval?.Status != WorkOrderApprovalStatus.Pending)
        {
            throw new InvalidOperationException(
                $"work order '{State.WorkOrderId}' is not waiting for approval.");
        }

        await PersistDomainEventAsync(new WorkOrderApprovalDecidedEvent
        {
            DecisionId = decisionId.Trim(),
            ApprovalStatus = decision,
            DecidedBy = decidedBy.Clone(),
            Reason = reason?.Trim() ?? string.Empty,
            DecidedAtUtc = decidedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private async Task RecordTerminalEvidenceAsync(
        WorkOrderTerminalEvidence evidence,
        string expectedPublisherActorId)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Outcome == WorkOrderTerminalOutcome.Unspecified)
            throw new InvalidOperationException("terminal evidence outcome is required.");
        EnsureAcceptedRunIdentity(
            evidence.DeliveryId,
            evidence.RunId,
            evidence.RunActorId,
            evidence.CommandId,
            evidence.CorrelationId);
        EnsureInboundPublisherMatches(expectedPublisherActorId, "Run terminal evidence");

        if (State.LifecycleStatus == WorkOrderLifecycleStatus.TimedOut)
        {
            if (EvidenceEqual(State.LateTerminalEvidence, evidence))
                return;
            if (State.LateTerminalEvidence != null)
                throw new InvalidOperationException("conflicting late terminal evidence was received.");

            await PersistDomainEventAsync(new WorkOrderLateTerminalEvidenceRecordedEvent
            {
                Evidence = evidence.Clone(),
            });
            return;
        }

        if (IsTerminal(State.LifecycleStatus))
        {
            if (EvidenceEqual(State.TerminalEvidence, evidence))
                return;
            throw new InvalidOperationException("conflicting terminal evidence was received.");
        }

        if (State.LifecycleStatus != WorkOrderLifecycleStatus.DispatchPending &&
            State.LifecycleStatus != WorkOrderLifecycleStatus.Running)
        {
            throw new InvalidOperationException(
                $"terminal evidence cannot advance work order from '{State.LifecycleStatus}'.");
        }

        await PersistDomainEventAsync(new WorkOrderTerminalEvidenceRecordedEvent
        {
            LifecycleStatus = evidence.Outcome switch
            {
                WorkOrderTerminalOutcome.Succeeded => WorkOrderLifecycleStatus.Completed,
                WorkOrderTerminalOutcome.Failed => WorkOrderLifecycleStatus.Failed,
                WorkOrderTerminalOutcome.Stopped => WorkOrderLifecycleStatus.Stopped,
                _ => throw new InvalidOperationException("terminal outcome is unsupported."),
            },
            Evidence = evidence.Clone(),
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
        };

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

    private Task PersistDispatchFailureAsync(string code, string message, string source) =>
        PersistDomainEventAsync(new WorkOrderDispatchFailedEvent
        {
            Failure = new WorkOrderFailureReference
            {
                Code = code,
                Message = message,
                Source = source,
                ReferenceId = State.DispatchCommandId,
            },
            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

    private void ValidateAcceptedExecution(WorkOrderExecutionAccepted accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (!string.Equals(accepted.RunId, State.RequestedRunId, StringComparison.Ordinal) ||
            !string.Equals(accepted.CommandId, State.DispatchCommandId, StringComparison.Ordinal) ||
            !string.Equals(accepted.CorrelationId, State.DispatchCommandId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(accepted.RunActorId))
        {
            throw new InvalidOperationException(
                "WorkOrder execution receipt does not match the authorized Run or command identity.");
        }
    }

    private void EnsureAcceptedRunIdentity(
        string deliveryId,
        string runId,
        string runActorId,
        string commandId,
        string correlationId)
    {
        if (State.Execution == null || string.IsNullOrWhiteSpace(State.Execution.RunId))
            throw new InvalidOperationException("Run evidence arrived before Run acceptance was recorded.");
        if (!string.Equals(State.TerminalDeliveryId, deliveryId, StringComparison.Ordinal) ||
            !string.Equals(State.Execution.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(State.Execution.RunActorId, runActorId, StringComparison.Ordinal) ||
            !string.Equals(State.Execution.CommandId, commandId, StringComparison.Ordinal) ||
            !string.Equals(State.Execution.CorrelationId, correlationId, StringComparison.Ordinal))
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
        var runId = State.Execution?.RunId;
        if (string.IsNullOrWhiteSpace(runId))
            return string.Empty;

        return ServiceRunIds.BuildActorId(State.ScopeId, State.PublishedServiceId, runId);
    }

    private IEnumerable<WorkOrderArtifactReference> CloneDeclaredResultArtifacts() =>
        State.Input?.DeclaredResultArtifacts.Select(static artifact => artifact.Clone()) ?? [];

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

    private void EnsureApprover(WorkOrderPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        EnsureRequired(principal.PrincipalId, "principalId");
        if (State.PermissionPlan?.ApproverPrincipalIds.Contains(principal.PrincipalId) != true)
            throw new InvalidOperationException("work order approval principal is not authorized by the permission plan.");
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

        if (RequiresApproval(command.PermissionPlan))
        {
            EnsureRequired(command.ApprovalId, nameof(command.ApprovalId));
            EnsureCanonicalIdentity(
                "approval_id",
                command.ApprovalId,
                WorkOrderConventions.BuildApprovalId(canonicalWorkOrderId));
            if (command.PermissionPlan.ApproverPrincipalIds.Count == 0)
                throw new InvalidOperationException("an approval-requiring permission plan must name an approver principal.");
        }
    }

    private static void EnsureCanonicalIdentity(string fieldName, string? actual, string expected)
    {
        EnsureRequired(actual, fieldName);
        if (!string.Equals(actual!.Trim(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{fieldName} must use canonical identity '{expected}'.");
    }

    private static bool RequiresApproval(WorkOrderPermissionPlan? plan) =>
        plan?.Requirements.Any(static requirement => requirement.RequiresApproval) == true;

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

    private static bool EvidenceEqual(WorkOrderTerminalEvidence? left, WorkOrderTerminalEvidence right) =>
        left != null && left.Equals(right);

    private static bool IsTerminal(WorkOrderLifecycleStatus status) =>
        status is WorkOrderLifecycleStatus.Completed or
            WorkOrderLifecycleStatus.Failed or
            WorkOrderLifecycleStatus.Stopped or
            WorkOrderLifecycleStatus.Denied or
            WorkOrderLifecycleStatus.Cancelled or
            WorkOrderLifecycleStatus.TimedOut;

    private static string BuildTimeoutCallbackId(string workOrderId, Timestamp timeoutAt) =>
        $"work-order-timeout-{workOrderId}-{timeoutAt.Seconds}-{timeoutAt.Nanos}";

    private static void EnsureRequired(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{fieldName} is required.");
    }

}
