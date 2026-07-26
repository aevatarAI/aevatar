using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgentService.Abstractions;
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
    private static readonly MethodInfo TransitionStateMethod = typeof(WorkOrderGAgent)
        .GetMethod("TransitionState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("WorkOrderGAgent.TransitionState was not found.");

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
    public async Task Reassign_WhenAssignmentAlreadyMatches_ShouldStillRejectStaleVersion()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate());
        await agent.HandleReassignAsync(BuildReassign(expectedVersion: 2));

        var stale = () => agent.HandleReassignAsync(BuildReassign(expectedVersion: 2));

        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lifecycle version is 3, not 2*");
    }

    [Fact]
    public async Task Reassign_WhenAssignmentAlreadyMatches_ShouldStillRejectAfterDispatch()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        var sameAssignment = new ReassignWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = agent.State.LifecycleVersion,
            RequestedBy = Principal("requester-1"),
            MemberId = agent.State.MemberId,
            PublishedServiceId = agent.State.PublishedServiceId,
            WorkflowId = agent.State.WorkflowId,
            ServiceRevisionId = agent.State.ServiceRevisionId,
            ImplementationKind = agent.State.ImplementationKind,
        };

        var reassign = () => agent.HandleReassignAsync(sameAssignment);

        await reassign.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be reassigned from*DispatchPending*");
    }

    [Fact]
    public async Task Cancel_WhenAlreadyCancelled_ShouldStillRejectStaleVersion()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(BuildCreate());
        var cancel = new CancelWorkOrder
        {
            WorkOrderId = WorkOrderId,
            ExpectedLifecycleVersion = 2,
            RequestedBy = Principal("requester-1"),
            Reason = "withdrawn",
        };
        await agent.HandleCancelAsync(cancel);

        var stale = () => agent.HandleCancelAsync(cancel.Clone());

        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lifecycle version is 3, not 2*");
    }

    [Fact]
    public async Task Dispatch_WhenSameDispatchAlreadyPending_ShouldStillRejectStaleVersion()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());

        var stale = () => agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));

        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*lifecycle version is 3, not 2*");
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
    public async Task CreateWorkOrder_WithoutDeadline_ShouldPersistReadyStateWithoutInventingDeadline()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        var command = BuildCreate();
        command.TimeoutAtUtc = null;

        await agent.HandleCreateAsync(command);

        agent.State.WorkOrderId.Should().Be(WorkOrderId);
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
        agent.State.LifecycleVersion.Should().Be(2);
        agent.State.TimeoutAtUtc.Should().BeNull();
        (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateWorkOrder_WhenDeadlineIsNotAfterRequestedAt_ShouldRejectBeforePersisting()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        var command = BuildCreate();
        command.TimeoutAtUtc = command.RequestedAtUtc.Clone();

        var create = () => agent.HandleCreateAsync(command);

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*timeout_at_utc*later than*requested_at_utc*");
        agent.State.WorkOrderId.Should().BeEmpty();
        (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAndRunOutcome_ShouldLinkRunWithoutCopyingTerminalPayload()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        });
        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Run.RunId.Should().Be(RequestedRunId);
        agent.State.Run.CommandId.Should().Be(DispatchCommandId);
        scheduler.Requests.Should().ContainSingle();

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
        agent.State.RunOutcome.RunId.Should().Be(RequestedRunId);
        agent.State.RunOutcome.CorrelationId.Should().Be(DispatchCommandId);
        agent.State.RunOutcome.Outcome.Should().Be(WorkOrderTerminalOutcome.Succeeded);
    }

    [Fact]
    public async Task AcceptedContinuation_ShouldRemainDispatchPendingUntilCommittedRunStart()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());

        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

        agent.State.Run.RunId.Should().Be(RequestedRunId);
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutionContinuation_ShouldRejectEnvelopeFromDifferentPublisher(bool accepted)
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        var lifecycleVersion = agent.State.LifecycleVersion;
        IMessage continuation = accepted
            ? BuildAcceptedContinuation(agent.State)
            : BuildFailedContinuation(agent.State);

        var act = () => agent.HandleEventAsync(
            BuildInboundEnvelope(continuation, "forged-execution-worker"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.Run.Should().BeNull();
        agent.State.Failure.Should().BeNull();
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("deployment")]
    [InlineData("acceptedAt")]
    public async Task AcceptedContinuation_WhenRunLinkIsIncompleteOrUnauthorized_ShouldReject(string invalidField)
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        var lifecycleVersion = agent.State.LifecycleVersion;
        var continuation = BuildAcceptedContinuation(agent.State);
        switch (invalidField)
        {
            case "revision":
                continuation.Accepted.RevisionId = "revision-unrelated";
                break;
            case "deployment":
                continuation.Accepted.DeploymentId = string.Empty;
                break;
            case "acceptedAt":
                continuation.Accepted.AcceptedAtUtc = null;
                break;
        }

        var act = () => agent.HandleExecutionAcceptedAsync(continuation);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution receipt*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.Run.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteInternalSignal_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);
        var lifecycleVersion = agent.State.LifecycleVersion;

        var act = () => agent.HandleEventAsync(BuildInboundEnvelope(
            new ExecuteWorkOrder
            {
                WorkOrderId = agent.State.WorkOrderId,
                DispatchCommandId = agent.State.DispatchCommandId,
            },
            "forged-signal-publisher"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        scheduler.Requests.Should().BeEmpty();
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.ExecutionRetryAttempt.Should().Be(0);
    }

    [Fact]
    public async Task ExecutionRetryInternalSignal_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        var retryAttempt = agent.State.ExecutionRetryAttempt;

        var act = () => agent.HandleEventAsync(BuildInboundEnvelope(
            new WorkOrderExecutionRetryFired
            {
                WorkOrderId = agent.State.WorkOrderId,
                DispatchCommandId = agent.State.DispatchCommandId,
                RequestedRunId = agent.State.RequestedRunId,
                Attempt = retryAttempt,
            },
            "forged-signal-publisher"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        scheduler.Requests.Should().ContainSingle();
        agent.State.ExecutionRetryAttempt.Should().Be(retryAttempt);
    }

    [Fact]
    public async Task TimeoutInternalSignal_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var requestedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var create = BuildCreate();
        create.RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt);
        create.TimeoutAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddMinutes(1));
        var agent = await CreateAgentAsync();
        await agent.HandleCreateAsync(create);
        var lifecycleVersion = agent.State.LifecycleVersion;

        var act = () => agent.HandleEventAsync(BuildInboundEnvelope(
            new WorkOrderTimeoutFired
            {
                WorkOrderId = agent.State.WorkOrderId,
                TimeoutAtUtc = agent.State.TimeoutAtUtc.Clone(),
            },
            "forged-signal-publisher"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Ready);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
    }

    [Fact]
    public async Task ExecuteWorkOrder_ShouldOnlyScheduleAndRemainDispatchPending()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        scheduler.Requests.Should().ContainSingle();
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Run.Should().BeNull();
    }

    [Fact]
    public async Task AcceptedContinuation_AfterTimeout_ShouldBeIgnored()
    {
        var agent = await CreateTimedOutDispatchAgentAsync();
        var version = agent.State.LifecycleVersion;

        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.TimedOut);
        agent.State.LifecycleVersion.Should().Be(version);
    }

    [Fact]
    public async Task DuplicateAcceptedContinuation_ShouldPersistNothingAndNotChangeLifecycleVersion()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            eventStore: eventStore);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        var continuation = BuildAcceptedContinuation(agent.State);
        await agent.HandleExecutionAcceptedAsync(continuation);
        var acceptedState = agent.State.Clone();
        var eventCount = (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Count;

        await agent.HandleExecutionAcceptedAsync(continuation.Clone());

        agent.State.Should().Be(acceptedState);
        agent.State.LifecycleVersion.Should().Be(acceptedState.LifecycleVersion);
        (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task FailedContinuation_AfterAccepted_ShouldBeIgnoredWithoutChangingState()
    {
        var eventStore = new InMemoryEventStore();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            eventStore: eventStore);
        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));
        var acceptedState = agent.State.Clone();
        var eventCount = (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Count;

        await agent.HandleExecutionFailedAsync(BuildFailedContinuation(agent.State));

        agent.State.Should().Be(acceptedState);
        agent.State.LifecycleVersion.Should().Be(acceptedState.LifecycleVersion);
        (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task ExecutionRetryFired_WhenAttemptIsStale_ShouldNotSchedule()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);

        await agent.HandleExecutionRetryFiredAsync(new WorkOrderExecutionRetryFired
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
            RequestedRunId = agent.State.RequestedRunId,
            Attempt = 99,
        });

        scheduler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWorkOrder_WhenQueueFull_ShouldScheduleDurableRetryWithoutFailing()
    {
        var scheduler = new RecordingExecutionScheduler(queueFull: true);
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler, callbackScheduler);
        var lifecycleVersion = agent.State.LifecycleVersion;

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.ExecutionRetryAttempt.Should().Be(1);
        agent.State.ExecutionRetryCallbackId.Should().NotBeEmpty();
        var retry = callbackScheduler.Timeouts.Should().ContainSingle().Subject;
        retry.CallbackId.Should().Be(agent.State.ExecutionRetryCallbackId);
        retry.TriggerEnvelope.Payload.Unpack<WorkOrderExecutionRetryFired>().Attempt.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWorkOrder_WhenQueueFullWithoutDeadline_ShouldScheduleDurableRetry()
    {
        var scheduler = new RecordingExecutionScheduler(queueFull: true);
        var callbackScheduler = new RecordingCallbackScheduler();
        var create = BuildCreate();
        create.TimeoutAtUtc = null;
        var agent = await CreateDispatchPendingAgentAsync(
            scheduler,
            callbackScheduler,
            create: create);

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        agent.State.TimeoutAtUtc.Should().BeNull();
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.ExecutionRetryAttempt.Should().Be(1);
        callbackScheduler.Timeouts.Should().ContainSingle()
            .Which.TriggerEnvelope.Payload.Unpack<WorkOrderExecutionRetryFired>().Attempt.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWorkOrder_WhenSchedulerIsMissing_ShouldRemainPendingAndScheduleRetry()
    {
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(null, callbackScheduler);
        var lifecycleVersion = agent.State.LifecycleVersion;

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.ExecutionRetryAttempt.Should().Be(1);
        callbackScheduler.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecutionRetry_ShouldStartAtTwoHundredFiftyMilliseconds()
    {
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            callbackScheduler);

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        callbackScheduler.Timeouts.Should().ContainSingle()
            .Which.DueTime.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task ExecutionRetry_ShouldCapExponentialDelayAtThirtySeconds()
    {
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            callbackScheduler);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        while (agent.State.ExecutionRetryAttempt < 8)
        {
            var fired = callbackScheduler.Timeouts[^1].TriggerEnvelope.Payload
                .Unpack<WorkOrderExecutionRetryFired>();
            await agent.HandleExecutionRetryFiredAsync(fired);
        }

        agent.State.ExecutionRetryAttempt.Should().Be(8);
        callbackScheduler.Timeouts[^1].DueTime.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ExecutionRetry_ShouldCapDelayAtRemainingDeadline()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var create = BuildCreate();
        create.RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt);
        create.TimeoutAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(5));
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            callbackScheduler,
            create: create);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        while (agent.State.ExecutionRetryAttempt < 6)
        {
            var fired = callbackScheduler.Timeouts[^1].TriggerEnvelope.Payload
                .Unpack<WorkOrderExecutionRetryFired>();
            await agent.HandleExecutionRetryFiredAsync(fired);
        }

        callbackScheduler.Timeouts[^1].DueTime.Should().BeLessThan(TimeSpan.FromSeconds(8));
        callbackScheduler.Timeouts[^1].DueTime.Should().BeGreaterThan(TimeSpan.Zero);
        agent.State.ExecutionRetryAtUtc.Should().Be(agent.State.TimeoutAtUtc);
    }

    [Fact]
    public async Task ExecutionRetry_WhenCallbackSchedulingFails_ShouldNotPersistRetryStateOrEvent()
    {
        var eventStore = new InMemoryEventStore();
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            callbackScheduler,
            eventStore);
        var lifecycleVersion = agent.State.LifecycleVersion;
        var eventCount = (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Count;
        callbackScheduler.ScheduleException = new InvalidOperationException("callback unavailable");

        var schedule = () => agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });

        await schedule.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("callback unavailable");
        agent.State.ExecutionRetryAttempt.Should().Be(0);
        agent.State.ExecutionRetryCallbackId.Should().BeEmpty();
        agent.State.ExecutionRetryAtUtc.Should().BeNull();
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None)).Should().HaveCount(eventCount);
    }

    [Fact]
    public async Task ExecutionWatchdog_WhenStillPending_ShouldReenqueueCanonicalRequest()
    {
        var scheduler = new RecordingExecutionScheduler();
        var callbackScheduler = new RecordingCallbackScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler, callbackScheduler);
        var lifecycleVersion = agent.State.LifecycleVersion;

        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        var watchdog = callbackScheduler.Timeouts.Should().ContainSingle().Subject
            .TriggerEnvelope.Payload.Unpack<WorkOrderExecutionRetryFired>();

        await agent.HandleExecutionRetryFiredAsync(watchdog);

        scheduler.Requests.Should().HaveCount(2);
        scheduler.Requests[1].Should().BeEquivalentTo(scheduler.Requests[0]);
        agent.State.ExecutionRetryAttempt.Should().Be(2);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
    }

    [Fact]
    public void ExecutionRetryScheduled_WhenCorrelationMismatches_ShouldBeIgnoredWithoutAdvancingLifecycle()
    {
        var current = BuildRetryState(attempt: 2);
        var mismatched = new WorkOrderExecutionRetryScheduledEvent
        {
            WorkOrderId = current.WorkOrderId,
            DispatchCommandId = "cmd-unrelated",
            RequestedRunId = current.RequestedRunId,
            Attempt = 3,
            CallbackId = "retry-unrelated",
            RetryAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = ApplyStateTransition(current, mismatched);

        next.Should().BeSameAs(current);
        next.LifecycleVersion.Should().Be(current.LifecycleVersion);
        next.ExecutionRetryAttempt.Should().Be(2);
    }

    [Fact]
    public void ExecutionRetryScheduled_WhenAttemptIsNonIncreasing_ShouldBeIgnoredWithoutAdvancingLifecycle()
    {
        var current = BuildRetryState(attempt: 2);
        var nonIncreasing = new WorkOrderExecutionRetryScheduledEvent
        {
            WorkOrderId = current.WorkOrderId,
            DispatchCommandId = current.DispatchCommandId,
            RequestedRunId = current.RequestedRunId,
            Attempt = 2,
            CallbackId = "retry-repeated",
            RetryAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var next = ApplyStateTransition(current, nonIncreasing);

        next.Should().BeSameAs(current);
        next.LifecycleVersion.Should().Be(current.LifecycleVersion);
        next.ExecutionRetryCallbackId.Should().Be(current.ExecutionRetryCallbackId);
    }

    [Fact]
    public async Task AcceptedContinuation_ShouldClearExecutionRetryFields()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        agent.State.ExecutionRetryAttempt.Should().Be(1);

        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

        AssertExecutionRetryCleared(agent.State);
    }

    [Fact]
    public async Task FailedContinuation_ShouldClearExecutionRetryFields()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        agent.State.ExecutionRetryAttempt.Should().Be(1);

        await agent.HandleExecutionFailedAsync(BuildFailedContinuation(agent.State));

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Failed);
        AssertExecutionRetryCleared(agent.State);
    }

    [Fact]
    public void TerminalOutcome_ShouldClearExecutionRetryFields()
    {
        var current = BuildRetryState(attempt: 4);
        current.LifecycleStatus = WorkOrderLifecycleStatus.Running;
        current.Run = new WorkOrderRunLink
        {
            RunId = RequestedRunId,
            RunActorId = "workflow-run-actor-1",
            CommandId = DispatchCommandId,
            CorrelationId = DispatchCommandId,
        };
        var terminal = new WorkOrderRunOutcomeObservedEvent
        {
            LifecycleStatus = WorkOrderLifecycleStatus.Completed,
            Outcome = new WorkOrderRunOutcomeReference
            {
                DeliveryId = TerminalDeliveryId,
                RunId = RequestedRunId,
                RunActorId = "workflow-run-actor-1",
                CommandId = DispatchCommandId,
                CorrelationId = DispatchCommandId,
                Outcome = WorkOrderTerminalOutcome.Succeeded,
                TerminalAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var next = ApplyStateTransition(current, terminal);

        next.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        next.LifecycleVersion.Should().Be(current.LifecycleVersion + 1);
        AssertExecutionRetryCleared(next);
    }

    [Fact]
    public async Task FailedContinuation_WhenCorrelationMismatches_ShouldBeIgnored()
    {
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler());
        var lifecycleVersion = agent.State.LifecycleVersion;
        var continuation = new WorkOrderExecutionFailedContinuation
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
            RequestedRunId = "run-unrelated",
            Failed = new WorkOrderExecutionFailed
            {
                Failure = new WorkOrderFailureReference
                {
                    Code = "WORK_ORDER_DISPATCH_FAILED",
                    Message = "unrelated failure",
                    Source = "test-worker",
                    ReferenceId = "delivery-unrelated",
                },
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        await agent.HandleExecutionFailedAsync(continuation);

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.LifecycleVersion.Should().Be(lifecycleVersion);
        agent.State.Failure.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowStarted_ShouldBeIdempotentAndRejectMismatchedCorrelation()
    {
        var agent = await CreateAcceptedDispatchAgentAsync();

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
        agent.State.LifecycleVersion.Should().Be(startedVersion);
    }

    [Fact]
    public async Task RunOutcomeBeforeStarted_ShouldRemainTerminalWhenStartedArrivesLate()
    {
        var agent = await CreateAcceptedDispatchAgentAsync();

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
        agent.State.RunOutcome.Outcome.Should().Be(WorkOrderTerminalOutcome.Succeeded);
        var terminalUpdatedAt = agent.State.UpdatedAtUtc.Clone();
        var terminalVersion = agent.State.LifecycleVersion;

        var started = BuildWorkflowStarted();
        await agent.HandleWorkflowStartedAsync(started);

        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.Completed);
        agent.State.UpdatedAtUtc.Should().Be(terminalUpdatedAt);
        agent.State.LifecycleVersion.Should().Be(terminalVersion);
    }

    [Fact]
    public async Task WorkflowTerminal_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var agent = await CreateAcceptedDispatchAgentAsync();

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
        agent.State.RunOutcome.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowStarted_ShouldRejectEnvelopeFromDifferentPublisher()
    {
        var agent = await CreateAcceptedDispatchAgentAsync();

        var act = () => agent.HandleEventAsync(
            BuildInboundEnvelope(BuildWorkflowStarted(), "forged-workflow-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
    }

    [Fact]
    public async Task ServiceRunTerminal_ShouldRejectEnvelopeFromNonCanonicalServiceRunPublisher()
    {
        var create = BuildCreate();
        create.ImplementationKind = "script";
        create.WorkflowId = string.Empty;
        var agent = await CreateAcceptedDispatchAgentAsync(create);
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
        agent.State.RunOutcome.Should().BeNull();
    }

    [Fact]
    public async Task RunOutcome_ShouldRejectMismatchedRunActorIdentity()
    {
        var agent = await CreateAcceptedDispatchAgentAsync();
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
        agent.State.RunOutcome.Should().BeNull();
    }

    [Fact]
    public async Task RedispatchAtCurrentVersion_ShouldNotCreateAnotherRun()
    {
        var scheduler = new RecordingExecutionScheduler();
        var agent = await CreateDispatchPendingAgentAsync(scheduler);
        var execute = new ExecuteWorkOrder
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
        };
        await agent.HandleExecuteAsync(execute);
        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

        await agent.HandleDispatchAsync(BuildDispatch(agent.State.LifecycleVersion));
        await agent.HandleExecuteAsync(execute.Clone());

        scheduler.Requests.Should().ContainSingle();
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        agent.State.Run.RunId.Should().Be(RequestedRunId);
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
    public async Task TimeoutThenRunOutcome_ShouldKeepTimedOutAndRecordLateReference()
    {
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-2));
        var past = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1));
        var create = BuildCreate();
        create.RequestedAtUtc = requestedAt;
        create.TimeoutAtUtc = past;
        var agent = await CreateDispatchPendingAgentAsync(new RecordingExecutionScheduler(), create: create);
        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));

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
        agent.State.RunOutcome.Should().BeNull();
        agent.State.LateRunOutcome.DeliveryId.Should().Be(TerminalDeliveryId);
        agent.State.LateRunOutcome.Outcome.Should().Be(WorkOrderTerminalOutcome.Failed);
    }

    [Fact]
    public async Task ActivateAsync_WhenDispatchPending_ShouldReenqueueAndRestoreWatchdog()
    {
        var eventStore = new InMemoryEventStore();
        await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            eventStore: eventStore);

        var scheduler = new RecordingExecutionScheduler();
        var callbackScheduler = new RecordingCallbackScheduler();
        var recovered = await CreateAgentAsync(
            eventStore: eventStore,
            executionScheduler: scheduler,
            callbackScheduler: callbackScheduler,
            activate: false);
        await recovered.ActivateAsync();

        recovered.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.DispatchPending);
        var request = scheduler.Requests.Should().ContainSingle().Subject;
        request.WorkOrderId.Should().Be(WorkOrderId);
        request.DispatchCommandId.Should().Be(DispatchCommandId);
        request.RequestedRunId.Should().Be(RequestedRunId);
        callbackScheduler.Timeouts.Should().Contain(request =>
            request.TriggerEnvelope.Payload.Is(WorkOrderExecutionRetryFired.Descriptor));
    }

    [Fact]
    public void ProtobufContracts_ShouldRoundTripDistinctIdentityAndRunReferenceFields()
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
            Run = new WorkOrderRunLink { RunId = "run-1" },
            ExecutionRetryAttempt = 3,
            ExecutionRetryCallbackId = "retry-3",
            ExecutionRetryAtUtc = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2099-01-01T00:00:03Z")),
            RunOutcome = new WorkOrderRunOutcomeReference
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
        restored.Run.RunId.Should().Be("run-1");
        restored.ExecutionRetryAttempt.Should().Be(3);
        restored.ExecutionRetryCallbackId.Should().Be("retry-3");
        restored.RunOutcome.DeliveryId.Should().Be("delivery-1");
        restored.RunOutcome.CorrelationId.Should().Be("correlation-1");
    }

    private static async Task<WorkOrderGAgent> CreateDispatchPendingAgentAsync(
        IWorkOrderExecutionScheduler? scheduler,
        RecordingCallbackScheduler? callbackScheduler = null,
        InMemoryEventStore? eventStore = null,
        CreateWorkOrder? create = null)
    {
        var callbacks = callbackScheduler ?? new RecordingCallbackScheduler();
        var agent = await CreateAgentAsync(
            eventStore: eventStore,
            executionScheduler: scheduler,
            callbackScheduler: callbacks);
        await agent.HandleCreateAsync(create ?? BuildCreate());
        await agent.HandleDispatchAsync(BuildDispatch(expectedVersion: 2));
        callbacks.Timeouts.Clear();
        return agent;
    }

    private static async Task<WorkOrderGAgent> CreateAcceptedDispatchAgentAsync(
        CreateWorkOrder? create = null)
    {
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            create: create);
        await agent.HandleExecuteAsync(new ExecuteWorkOrder
        {
            WorkOrderId = agent.State.WorkOrderId,
            DispatchCommandId = agent.State.DispatchCommandId,
        });
        await agent.HandleExecutionAcceptedAsync(BuildAcceptedContinuation(agent.State));
        return agent;
    }

    private static async Task<WorkOrderGAgent> CreateTimedOutDispatchAgentAsync()
    {
        var create = BuildCreate();
        create.RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-2));
        create.TimeoutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1));
        var agent = await CreateDispatchPendingAgentAsync(
            new RecordingExecutionScheduler(),
            create: create);
        await agent.HandleTimeoutAsync(new WorkOrderTimeoutFired
        {
            WorkOrderId = agent.State.WorkOrderId,
            TimeoutAtUtc = agent.State.TimeoutAtUtc.Clone(),
        });
        agent.State.LifecycleStatus.Should().Be(WorkOrderLifecycleStatus.TimedOut);
        return agent;
    }

    private static async Task<WorkOrderGAgent> CreateAgentAsync(
        string? actorId = null,
        InMemoryEventStore? eventStore = null,
        IWorkOrderExecutionScheduler? executionScheduler = null,
        RecordingCallbackScheduler? callbackScheduler = null,
        IEventPublisher? publisher = null,
        bool activate = true)
    {
        var agent = new WorkOrderGAgent(executionScheduler)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkOrderState>(
                eventStore ?? new InMemoryEventStore()),
            EventPublisher = publisher ?? new RecordingEventPublisher(),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .AddSingleton<IActorRuntimeCallbackScheduler>(
                    callbackScheduler ?? new RecordingCallbackScheduler())
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [actorId ?? ActorId]);
        if (activate)
            await agent.ActivateAsync();
        return agent;
    }

    private static CreateWorkOrder BuildCreate()
    {
        var requestedAt = DateTimeOffset.UtcNow;
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
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            TimeoutAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddHours(1)),
            ExpectedLifecycleVersion = 0,
        };
        command.Input.DeclaredResultArtifacts.Add(new WorkOrderArtifactReference
        {
            ArtifactId = "result-1",
            ArtifactKind = "report",
        });
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

    private static WorkOrderExecutionAcceptedContinuation BuildAcceptedContinuation(
        WorkOrderState state) =>
        new()
        {
            WorkOrderId = state.WorkOrderId,
            DispatchCommandId = state.DispatchCommandId,
            RequestedRunId = state.RequestedRunId,
            Accepted = new WorkOrderExecutionAccepted
            {
                RunId = state.RequestedRunId,
                RunActorId = "workflow-run-actor-1",
                CommandId = state.DispatchCommandId,
                CorrelationId = state.DispatchCommandId,
                RevisionId = state.ServiceRevisionId,
                DeploymentId = "deployment-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

    private static WorkOrderExecutionFailedContinuation BuildFailedContinuation(
        WorkOrderState state) =>
        new()
        {
            WorkOrderId = state.WorkOrderId,
            DispatchCommandId = state.DispatchCommandId,
            RequestedRunId = state.RequestedRunId,
            Failed = new WorkOrderExecutionFailed
            {
                Failure = new WorkOrderFailureReference
                {
                    Code = "WORK_ORDER_DISPATCH_FAILED",
                    Message = "execution failed",
                    Source = "test-worker",
                    ReferenceId = state.DispatchCommandId,
                },
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

    private static WorkOrderState BuildRetryState(int attempt) =>
        new()
        {
            WorkOrderId = WorkOrderId,
            DispatchCommandId = DispatchCommandId,
            RequestedRunId = RequestedRunId,
            LifecycleStatus = WorkOrderLifecycleStatus.DispatchPending,
            LifecycleVersion = 7,
            ExecutionRetryAttempt = attempt,
            ExecutionRetryCallbackId = $"retry-{attempt}",
            ExecutionRetryAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static WorkOrderState ApplyStateTransition(WorkOrderState state, IMessage evt) =>
        (WorkOrderState)(TransitionStateMethod.Invoke(new WorkOrderGAgent(), [state, evt])
            ?? throw new InvalidOperationException("WorkOrder state transition returned null."));

    private static void AssertExecutionRetryCleared(WorkOrderState state)
    {
        state.ExecutionRetryAttempt.Should().Be(0);
        state.ExecutionRetryCallbackId.Should().BeEmpty();
        state.ExecutionRetryAtUtc.Should().BeNull();
    }

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

    private sealed class RecordingExecutionScheduler(bool queueFull = false)
        : IWorkOrderExecutionScheduler
    {
        public List<WorkOrderExecutionRequest> Requests { get; } = [];

        public ValueTask ScheduleAsync(
            WorkOrderExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (queueFull)
                throw new WorkOrderExecutionQueueFullException("WorkOrder execution queue is full.");

            Requests.Add(request.Clone());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Exception? ScheduleException { get; set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ScheduleException != null)
                throw ScheduleException;

            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

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
