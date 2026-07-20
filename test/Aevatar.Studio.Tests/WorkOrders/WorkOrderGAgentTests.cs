using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderGAgentTests
{
    private const string ScopeId = "scope-1";
    private const string DedupKey = "logical-work-1";
    private static readonly string WorkOrderId = WorkOrderConventions.BuildWorkOrderId(ScopeId, DedupKey);
    private static readonly string ActorId = WorkOrderConventions.BuildActorId(ScopeId, WorkOrderId);
    private static readonly string DispatchCommandId = WorkOrderConventions.BuildDispatchCommandId(WorkOrderId);
    private static readonly string RequestedRunId = WorkOrderConventions.BuildRequestedRunId(WorkOrderId);
    private static readonly string TerminalDeliveryId = WorkOrderConventions.BuildTerminalDeliveryId(WorkOrderId);

    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task CreateAndApprove_ShouldPreserveSeparateIdentitiesAndAdvanceVersion()
    {
        var agent = await CreateAgentAsync();
        var create = BuildCreate(requiresApproval: true);

        await agent.HandleCreateAsync(create);

        agent.State.WorkOrderId.Should().Be(WorkOrderId);
        agent.State.ScopeId.Should().Be(ScopeId);
        agent.State.TeamId.Should().Be("team-1");
        agent.State.Requester.PrincipalId.Should().Be("requester-1");
        agent.State.MemberId.Should().Be("member-1");
        agent.State.WorkflowId.Should().Be("workflow-1");
        agent.State.PublishedServiceId.Should().Be("service-1");
        agent.State.Approval.ApprovalId.Should().Be(WorkOrderConventions.BuildApprovalId(WorkOrderId));
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.WaitingApproval);
        agent.State.LifecycleVersion.Should().Be(2);

        await agent.HandleApproveAsync(new ApproveWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            DecisionId = "decision-1",
            DecidedBy = Principal("approver-1"),
            Reason = "approved",
        });

        agent.State.Approval.Status.Should().Be(WorkOrderApprovalStatus.Approved);
        agent.State.Approval.DecisionId.Should().Be("decision-1");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
        agent.State.LifecycleVersion.Should().Be(3);
    }

    [Fact]
    public async Task Deny_ShouldBecomeTerminalWithoutAuthorizingDispatch()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate(requiresApproval: true));

        await agent.HandleDenyAsync(new DenyWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            DecisionId = "decision-denied",
            DecidedBy = Principal("approver-1"),
            Reason = "not authorized",
        });

        agent.State.Approval.Status.Should().Be(WorkOrderApprovalStatus.Denied);
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Denied);
        agent.State.TerminalReason.Should().Be("not authorized");
    }

    [Fact]
    public async Task Approval_ShouldContinueAfterActorRestart()
    {
        var eventStore = new InMemoryEventStore();
        var original = await CreateAgentAsync(eventStore: eventStore);
        await original.HandleCreateAsync(BuildCreate(requiresApproval: true));

        var recovered = await CreateAgentAsync(eventStore: eventStore);
        await recovered.HandleApproveAsync(new ApproveWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            DecisionId = "decision-after-restart",
            DecidedBy = Principal("approver-1"),
            Reason = "approved after recovery",
        });

        recovered.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
        recovered.State.Approval.Status.Should().Be(WorkOrderApprovalStatus.Approved);
        recovered.State.Approval.DecisionId.Should().Be("decision-after-restart");
        recovered.State.Approval.DecidedBy.PrincipalId.Should().Be("approver-1");
    }

    [Fact]
    public async Task Reassign_ShouldRejectStaleConcurrentCommand()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate());

        await agent.HandleReassignAsync(BuildReassign(expectedVersion: 2));

        var stale = () => agent.HandleCancelAsync(new CancelWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            RequestedBy = Principal("requester-1"),
        });
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lifecycle version is 3, not 2*");
        agent.State.MemberId.Should().Be("member-2");
        agent.State.PublishedServiceId.Should().Be("service-2");
    }

    [Fact]
    public async Task DuplicateCreate_ShouldRemainIdempotentAfterReassignment()
    {
        var agent = await CreateAgentAsync();
        var create = BuildCreate();
        await agent.HandleCreateAsync(create);
        await agent.HandleReassignAsync(BuildReassign(expectedVersion: 2));

        await agent.HandleCreateAsync(create.Clone());

        agent.State.LifecycleVersion.Should().Be(3);
        agent.State.MemberId.Should().Be("member-2");
        agent.State.PublishedServiceId.Should().Be("service-2");
    }

    [Fact]
    public async Task ConflictingCreate_ShouldFailClosedWithoutChangingOriginalRequest()
    {
        var agent = await CreateAgentAsync();
        var create = BuildCreate();
        await agent.HandleCreateAsync(create);
        var conflictingCreate = create.Clone();
        conflictingCreate.Intent = "different work under the same logical identity";

        var conflict = () => agent.HandleCreateAsync(conflictingCreate);

        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*logical identity already exists with a different request*");
        agent.State.LifecycleVersion.Should().Be(2);
        agent.State.Intent.Should().Be(create.Intent);
        agent.State.CreationRequest.Should().BeEquivalentTo(create);
    }

    [Fact]
    public async Task Create_ShouldRejectWorkOrderIdThatDoesNotMatchScopeAndDedupKey()
    {
        var command = BuildCreate();
        command.WorkOrderId = "wo-noncanonical";
        var agent = await CreateAgentAsync(
            actorId: WorkOrderConventions.BuildActorId(command.ScopeId, command.WorkOrderId));

        var create = () => agent.HandleCreateAsync(command);

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*canonical*scope*dedup*");
    }

    [Fact]
    public async Task Create_ShouldRejectActorThatDoesNotOwnCanonicalWorkOrderIdentity()
    {
        var command = BuildCreate();
        command.WorkOrderId = WorkOrderConventions.BuildWorkOrderId(command.ScopeId, command.DedupKey);
        var agent = await CreateAgentAsync(actorId: "work-order:scope-1:different-work-order");

        var create = () => agent.HandleCreateAsync(command);

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*actor*canonical*identity*");
    }

    [Fact]
    public async Task Create_ShouldRequireRequesterPrincipalKind()
    {
        var command = BuildCreate();
        command.WorkOrderId = WorkOrderConventions.BuildWorkOrderId(command.ScopeId, command.DedupKey);
        command.Requester.PrincipalKind = string.Empty;
        var agent = await CreateAgentAsync(
            actorId: WorkOrderConventions.BuildActorId(command.ScopeId, command.WorkOrderId));

        var create = () => agent.HandleCreateAsync(command);

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requester.principal_kind*required*");
    }

    [Fact]
    public async Task DispatchAndTerminalEvidence_ShouldLinkRunAndDeclaredArtifacts()
    {
        var executionPort = new RecordingExecutionPort();
        var agent = await CreateAgentAsync(executionPort: executionPort);
        await agent.HandleCreateAsync(BuildCreate());
        var dispatch = BuildDispatch(expectedVersion: 2);

        await agent.HandleDispatchAsync(dispatch);
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Execution.RunId.Should().Be(RequestedRunId);
        agent.State.Execution.CommandId.Should().Be(DispatchCommandId);
        executionPort.Requests.Should().ContainSingle();

        await agent.HandleWorkflowStartedAsync(BuildWorkflowStarted());

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Running);

        await agent.HandleWorkflowTerminalAsync(new WorkflowRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "workflow-run-actor-1",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            Status = WorkflowRunTerminalStatus.Completed,
            Output = "done",
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T10:00:00Z")),
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        agent.State.TerminalEvidence.RunId.Should().Be(RequestedRunId);
        agent.State.TerminalEvidence.CorrelationId.Should().Be(DispatchCommandId);
        agent.State.TerminalEvidence.Output.Should().Be("done");
        agent.State.TerminalEvidence.ResultArtifacts.Should().ContainSingle()
            .Which.ArtifactId.Should().Be("result-1");
    }

    [Fact]
    public async Task AcceptedExecution_ShouldRemainDispatchPendingUntilCommittedRunStart()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        agent.State.Execution.RunId.Should().Be(RequestedRunId);
        agent.State.Execution.StartedAtUtc.Should().BeNull();
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
    }

    [Fact]
    public async Task WorkflowStarted_ShouldBeIdempotentAndRejectMismatchedCorrelation()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        var mismatched = BuildWorkflowStarted();
        mismatched.WorkflowCorrelationId = "different-correlation";
        var recordMismatched = () => agent.HandleWorkflowStartedAsync(mismatched);

        await recordMismatched.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match*Run identity*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);

        var started = BuildWorkflowStarted();
        await agent.HandleWorkflowStartedAsync(started);
        var startedVersion = agent.State.LifecycleVersion;
        await agent.HandleWorkflowStartedAsync(started.Clone());

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Running);
        agent.State.Execution.StartedAtUtc.Should().Be(started.StartedAt);
        agent.State.LifecycleVersion.Should().Be(startedVersion);
    }

    [Fact]
    public async Task TerminalEvidenceBeforeStarted_ShouldConvergeWithoutInventingStartTime()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        await agent.HandleWorkflowTerminalAsync(new WorkflowRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "workflow-run-actor-1",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            Status = WorkflowRunTerminalStatus.Completed,
            Output = "completed before started delivery",
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T10:00:00Z")),
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        agent.State.Execution.StartedAtUtc.Should().BeNull();
        var terminalUpdatedAt = agent.State.UpdatedAtUtc.Clone();

        var started = BuildWorkflowStarted();
        await agent.HandleWorkflowStartedAsync(started);

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        agent.State.Execution.StartedAtUtc.Should().Be(started.StartedAt);
        agent.State.UpdatedAtUtc.Should().Be(terminalUpdatedAt);
    }

    [Fact]
    public async Task WorkflowTerminal_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        var terminal = new WorkflowRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "workflow-run-actor-1",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            Status = WorkflowRunTerminalStatus.Completed,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T10:00:00Z")),
        };

        var act = () => agent.HandleEventAsync(BuildInboundEnvelope(terminal, "forged-workflow-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.TerminalEvidence.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowStarted_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        var act = () => agent.HandleEventAsync(
            BuildInboundEnvelope(BuildWorkflowStarted(), "forged-workflow-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Execution.StartedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ServiceRunTerminal_ShouldRejectEnvelopeFromNonCanonicalServiceRunPublisher()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        var create = BuildCreate();
        create.ImplementationKind = "script";
        create.WorkflowId = string.Empty;
        await agent.HandleCreateAsync(create);
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });
        var terminal = new ServiceRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            RunId = RequestedRunId,
            TargetActorId = "workflow-run-actor-1",
            CommandId = DispatchCommandId,
            CorrelationId = DispatchCommandId,
            Status = ServiceRunStatus.Completed,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T10:00:00Z")),
        };

        var act = () => agent.HandleEventAsync(
            BuildInboundEnvelope(terminal, "forged-service-run-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.TerminalEvidence.Should().BeNull();
    }

    [Fact]
    public async Task TerminalEvidence_ShouldRejectMismatchedRunActorIdentity()
    {
        var agent = await CreateAgentAsync(executionPort: new RecordingExecutionPort());
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });
        await agent.HandleWorkflowStartedAsync(BuildWorkflowStarted());

        var record = () => agent.HandleWorkflowTerminalAsync(new WorkflowRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "different-workflow-run-actor",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            Status = WorkflowRunTerminalStatus.Completed,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T10:00:00Z")),
        });

        await record.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match*Run identity*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Running);
        agent.State.TerminalEvidence.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateDispatchAndExecute_ShouldNotCreateAnotherRun()
    {
        var executionPort = new RecordingExecutionPort();
        var agent = await CreateAgentAsync(executionPort: executionPort);
        await agent.HandleCreateAsync(BuildCreate());
        var dispatch = BuildDispatch(expectedVersion: 2);
        await agent.HandleDispatchAsync(dispatch);
        var execute = new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        };
        await agent.HandleExecuteAsync(execute);

        await agent.HandleDispatchAsync(dispatch.Clone());
        await agent.HandleExecuteAsync(execute.Clone());

        executionPort.Requests.Should().ContainSingle();
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Execution.RunId.Should().Be(RequestedRunId);
    }

    [Fact]
    public async Task Dispatch_ShouldRejectNonCanonicalDerivedIdentities()
    {
        const string scopeId = "scope-derived-identity";
        const string dedupKey = "logical-derived-identity";
        var workOrderId = WorkOrderConventions.BuildWorkOrderId(scopeId, dedupKey);
        var agent = await CreateAgentAsync(
            actorId: WorkOrderConventions.BuildActorId(scopeId, workOrderId));
        var create = BuildCreate();
        create.ScopeId = scopeId;
        create.DedupKey = dedupKey;
        create.WorkOrderId = workOrderId;
        create.ApprovalId = WorkOrderConventions.BuildApprovalId(workOrderId);
        await agent.HandleCreateAsync(create);
        var dispatch = new DispatchWorkOrder
        {
            WorkOrderId = workOrderId,
            ExpectedLifecycleVersion = 2,
            RequestedBy = create.Requester.Clone(),
            DispatchCommandId = WorkOrderConventions.BuildDispatchCommandId(workOrderId),
            RequestedRunId = "different-run",
            TerminalDeliveryId = WorkOrderConventions.BuildTerminalDeliveryId(workOrderId),
        };

        var dispatchCommand = () => agent.HandleDispatchAsync(dispatch);

        await dispatchCommand.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requested_run_id*canonical*");
    }

    [Fact]
    public async Task Cancel_ShouldBeAllowedOnlyBeforeDispatchAuthorization()
    {
        var beforeDispatch = await CreateAgentAsync();
        await beforeDispatch.HandleCreateAsync(BuildCreate());
        await beforeDispatch.HandleCancelAsync(new CancelWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            RequestedBy = Principal("requester-1"),
            Reason = "withdrawn",
        });
        beforeDispatch.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Cancelled);

        var afterDispatch = await CreateAgentAsync();
        await afterDispatch.HandleCreateAsync(BuildCreate());
        await afterDispatch.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        var cancel = () => afterDispatch.HandleCancelAsync(new CancelWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 3,
            RequestedBy = Principal("requester-1"),
        });
        await cancel.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be cancelled after dispatch authorization*");
    }

    [Fact]
    public async Task TimeoutThenTerminalEvidence_ShouldKeepTimedOutAndRecordLateEvidence()
    {
        var past = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1));
        var executionPort = new RecordingExecutionPort();
        var agent = await CreateAgentAsync(executionPort: executionPort);
        var create = BuildCreate();
        create.TimeoutAtUtc = past;
        await agent.HandleCreateAsync(create);
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });

        await agent.HandleTimeoutAsync(new WorkOrderTimeoutFired
        {
            WorkOrderId = WorkOrderId,
            TimeoutAtUtc = past.Clone(),
        });
        await agent.HandleWorkflowTerminalAsync(new WorkflowRunTerminalNotification
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "workflow-run-actor-1",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            Status = WorkflowRunTerminalStatus.Failed,
            Error = "late failure",
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.TimedOut);
        agent.State.TerminalEvidence.Should().BeNull();
        agent.State.LateTerminalEvidence.Error.Should().Be("late failure");
    }

    [Fact]
    public async Task ActivateAsync_ShouldRedrivePersistedDispatchPendingWithSameIdentity()
    {
        var eventStore = new InMemoryEventStore();
        var original = await CreateAgentAsync(eventStore: eventStore);
        await original.HandleCreateAsync(BuildCreate());
        await original.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));

        var publisher = new RecordingEventPublisher();
        var recovered = await CreateAgentAsync(
            eventStore: eventStore,
            publisher: publisher,
            activate: false);
        await recovered.ActivateAsync();

        recovered.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        publisher.Sends.Should().ContainSingle();
        publisher.Sends[0].TargetActorId.Should().Be(ActorId);
        var execute = publisher.Sends[0].Message.Should().BeOfType<ExecuteWorkOrder>().Subject;
        execute.WorkOrderId.Should().Be(WorkOrderId);
        execute.DispatchCommandId.Should().Be(DispatchCommandId);
    }

    [Fact]
    public void ProtobufContracts_ShouldRoundTripDistinctIdentityAndEvidenceFields()
    {
        var state = new WorkOrderState
        {
            WorkOrderId = WorkOrderId,
            ScopeId = "scope-1",
            TeamId = "team-1",
            Requester = Principal("requester-1"),
            MemberId = "member-1",
            WorkflowId = "workflow-1",
            PublishedServiceId = "service-1",
            Approval = new WorkOrderApprovalState { ApprovalId = "approval-1" },
            Execution = new WorkOrderExecutionProvenance { RunId = "run-1" },
            TerminalEvidence = new WorkOrderTerminalEvidence
            {
                DeliveryId = "delivery-1",
                RunId = "run-1",
                CorrelationId = "correlation-1",
                Outcome = WorkOrderTerminalOutcome.Succeeded,
            },
        };

        var restored = WorkOrderState.Parser.ParseFrom(state.ToByteArray());

        restored.Requester.PrincipalId.Should().Be("requester-1");
        restored.MemberId.Should().Be("member-1");
        restored.WorkflowId.Should().Be("workflow-1");
        restored.PublishedServiceId.Should().Be("service-1");
        restored.Approval.ApprovalId.Should().Be("approval-1");
        restored.Execution.RunId.Should().Be("run-1");
        restored.TerminalEvidence.DeliveryId.Should().Be("delivery-1");
        restored.TerminalEvidence.CorrelationId.Should().Be("correlation-1");
    }

    private static async Task<WorkOrderGAgent> CreateAgentAsync(
        string? actorId = null,
        InMemoryEventStore? eventStore = null,
        IWorkOrderExecutionPort? executionPort = null,
        IEventPublisher? publisher = null,
        bool activate = true)
    {
        var agent = new WorkOrderGAgent(executionPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkOrderState>(
                eventStore ?? new InMemoryEventStore()),
            EventPublisher = publisher ?? new RecordingEventPublisher(),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [actorId ?? ActorId]);
        if (activate)
            await agent.ActivateAsync();
        return agent;
    }

    private static CreateWorkOrder BuildCreate(bool requiresApproval = false)
    {
        var command = new CreateWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DedupKey = DedupKey,
            ScopeId = ScopeId,
            TeamId = "team-1",
            Requester = Principal("requester-1"),
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = "workflow",
            EndpointId = "run",
            Intent = "complete the requested work",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "do the work" },
            },
            PermissionPlan = new WorkOrderPermissionPlan(),
            ApprovalId = WorkOrderConventions.BuildApprovalId(WorkOrderId),
            ExpectedLifecycleVersion = 0,
        };
        command.Input.DeclaredResultArtifacts.Add(new WorkOrderArtifactReference
        {
            ArtifactId = "result-1",
            ArtifactKind = "report",
        });
        if (requiresApproval)
        {
            command.PermissionPlan.ExternalActions.Add(new WorkOrderExternalActionReference
            {
                ActionId = "action-1",
                System = "github",
                Action = "write",
                ResourceId = "repo-1",
            });
            command.PermissionPlan.Requirements.Add(new WorkOrderPermissionRequirement
            {
                PermissionId = "permission-1",
                ActionId = "action-1",
                Capability = "repository.write",
                RequiresApproval = true,
            });
            command.PermissionPlan.ApproverPrincipalIds.Add("approver-1");
        }
        return command;
    }

    private static ReassignWorkOrder BuildReassign(long expectedVersion) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = expectedVersion,
            RequestedBy = Principal("requester-1"),
            MemberId = "member-2",
            PublishedServiceId = "service-2",
            WorkflowId = "workflow-2",
            ServiceRevisionId = "revision-2",
            ImplementationKind = "workflow",
        };

    private static DispatchWorkOrder BuildDispatch(long expectedVersion) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = expectedVersion,
            RequestedBy = Principal("requester-1"),
            DispatchCommandId = DispatchCommandId,
            RequestedRunId = RequestedRunId,
            TerminalDeliveryId = TerminalDeliveryId,
        };

    private static WorkflowRunStartedNotification BuildWorkflowStarted() =>
        new()
        {
            DeliveryId = TerminalDeliveryId,
            WorkflowRunId = RequestedRunId,
            WorkflowActorId = "workflow-run-actor-1",
            WorkflowCommandId = DispatchCommandId,
            WorkflowCorrelationId = DispatchCommandId,
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T09:59:00Z")),
        };

    private static EventEnvelope BuildInboundEnvelope(IMessage payload, string publisherActorId) =>
        new()
        {
            Id = $"test-{Guid.NewGuid():N}",
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, ActorId),
        };

    private static WorkOrderPrincipal Principal(string principalId) =>
        new()
        {
            PrincipalId = principalId,
            PrincipalKind = "user",
        };

    private sealed class RecordingExecutionPort : IWorkOrderExecutionPort
    {
        public List<WorkOrderExecutionRequest> Requests { get; } = [];

        public Task<WorkOrderExecutionResult> ExecuteAsync(
            WorkOrderExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request.Clone());
            return Task.FromResult(new WorkOrderExecutionResult
            {
                Accepted = new WorkOrderExecutionAccepted
                {
                    RunId = request.RequestedRunId,
                    RunActorId = "workflow-run-actor-1",
                    CommandId = request.DispatchCommandId,
                    CorrelationId = request.DispatchCommandId,
                    RevisionId = request.ServiceRevisionId,
                    DeploymentId = "deployment-1",
                    AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            });
        }
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<SentMessage> Sends { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage => Task.CompletedTask;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            Sends.Add(new SentMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(string TargetActorId, IMessage Message);
}
