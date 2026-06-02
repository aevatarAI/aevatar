using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests;

public sealed class ScheduledDispatchGAgentTests
{
    private const string ScheduleActorId = "scheduled-dispatch:schedule-1";
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";

    [Fact]
    public async Task HandleFireAsync_ShouldSuppressDuplicateDispatchAfterStartedRecordIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenEnabled_ShouldRegisterDurableNextFireCallback()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        scheduler.TimeoutRequests.Should().ContainSingle();
        var request = scheduler.TimeoutRequests[0];
        request.ActorId.Should().Be(ScheduleActorId);
        request.CallbackId.Should().Be(NextFireCallbackId);
        request.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.FiredSelfEvent);
        request.DueTime.Should().BePositive();
        request.DueTime.Should().BeLessThan(TimeSpan.FromSeconds(70));

        var fireCommand = request.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        fireCommand.Manual.Should().BeFalse();
        fireCommand.ScheduledFireAt.Should().NotBeNull();
        var scheduledFireAt = fireCommand.ScheduledFireAt.ToDateTimeOffset();
        scheduledFireAt.Should().Be(agent.State.NextFireAt);

        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.NextFireLease!.ActorId.Should().Be(ScheduleActorId);
        agent.State.NextFireLease.CallbackId.Should().Be(NextFireCallbackId);
        agent.State.NextFireLease.Generation.Should().Be(1);
        agent.State.NextFireLease.Backend.Should().Be(ScheduledDispatchRuntimeCallbackBackendState.Dedicated);
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenNextFirePersistFails_ShouldCancelNewLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        eventStore.ThrowOnAppendEventType = ScheduledDispatchNextFireScheduledEvent.Descriptor.FullName;

        var act = () => agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await act.Should().ThrowAsync<InvalidOperationException>();
        scheduler.TimeoutRequests.Should().ContainSingle();
        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleDisableAsync_ShouldCancelExistingLeaseBeforeDisabledStateClearsIt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleDisableAsync(new ScheduledDispatchDisableCommand
        {
            Reason = "pause",
        });

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        agent.State.Enabled.Should().BeFalse();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleDisableAsync_WhenPersistFails_ShouldNotCancelExistingLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));
        eventStore.ThrowOnAppend = true;

        var act = () => agent.HandleDisableAsync(new ScheduledDispatchDisableCommand
        {
            Reason = "pause",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        scheduler.Canceled.Should().BeEmpty();
        agent.State.Enabled.Should().BeTrue();
        agent.State.NextFireLease.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenUpdatingToDisabled_ShouldCancelExistingLeaseBeforeConfiguredStateClearsIt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "target-actor-updated",
            cronExpression: "*/5 * * * *",
            enabled: false));

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
        agent.State.Enabled.Should().BeFalse();
        agent.State.TargetActorId.Should().Be("target-actor-updated");
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
    }

    [Fact]
    public async Task HandleEventAsync_WhenDueCallbackArrives_ShouldDispatchNonManualFireAndScheduleNext()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        var firstRequest = scheduler.TimeoutRequests.Single();
        var firstFireCommand = firstRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        var firstScheduledFireAt = firstFireCommand.ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(firstRequest, generation: 1, fireIndex: 1));

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", firstScheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("target-actor-1");
        dispatched.Envelope.Id.Should().Be(idempotencyKey);
        dispatched.Envelope.Route.GetTargetActorId().Should().Be("target-actor-1");
        var chatRequest = dispatched.Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.SessionId.Should().Be(idempotencyKey);
        chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId].Should().Be(idempotencyKey);
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(firstScheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);

        agent.State.FireCount.Should().Be(1);
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        var fireRecord = agent.State.FireRecords[idempotencyKey];
        fireRecord.Manual.Should().BeFalse();
        fireRecord.Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);

        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].ActorId.Should().Be(ScheduleActorId);
        scheduler.Canceled[0].CallbackId.Should().Be(NextFireCallbackId);
        scheduler.Canceled[0].Generation.Should().Be(1);

        scheduler.TimeoutRequests.Should().HaveCount(2);
        var nextRequest = scheduler.TimeoutRequests[1];
        var nextFireCommand = nextRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        nextFireCommand.Manual.Should().BeFalse();
        nextFireCommand.ScheduledFireAt.ToDateTimeOffset().Should().BeAfter(firstScheduledFireAt);
        agent.State.NextFireLease!.Generation.Should().Be(2);
    }

    [Fact]
    public async Task HandleFireAsync_WithWorkflowStartRequest_ShouldResolveRunActorPerFireAndRebuildCommandEnvelope()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var resolver = new RecordingWorkflowRunActorResolver();
        var envelopeFactory = new RecordingWorkflowChatEnvelopeFactory();
        var agent = CreateAgent(eventStore, dispatch, workflowRunActorResolver: resolver, workflowChatEnvelopeFactory: envelopeFactory);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "workflow-schedule-target",
            triggerEnvelope: CreateTriggerEnvelope("schedule-1", new WorkflowScheduledDispatchStartRequest
            {
                ScheduleId = "schedule-1",
                WorkflowName = "daily-workflow",
                Prompt = "run daily",
                ScopeId = "scope-1",
                ActorId = "definition-actor-1",
                Headers =
                {
                    ["x-trace"] = "trace-1",
                    ["workflow.schedule_id"] = "schedule-1",
                },
            }),
            enabled: false));

        var firstFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        var secondFireAt = firstFireAt.AddMinutes(15);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(firstFireAt),
            Manual = true,
        });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(secondFireAt),
            Manual = true,
        });

        resolver.Requests.Should().HaveCount(2);
        resolver.Requests.Select(x => x.SessionId).Should().OnlyHaveUniqueItems();
        resolver.Requests[0].SessionId.Should().Be(ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", firstFireAt));
        resolver.Requests[1].SessionId.Should().Be(ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", secondFireAt));
        resolver.Requests[0].Source.Kind.Should().Be(WorkflowChatSourceKind.DefinitionActor);
        resolver.Requests[0].Source.ActorId.Should().Be("definition-actor-1");
        resolver.Requests[0].Metadata![ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        resolver.Requests[0].Metadata![ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(firstFireAt.ToUniversalTime().ToString("O"));
        resolver.Requests[0].Metadata![ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(resolver.Requests[0].SessionId);

        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches.Select(x => x.ActorId).Should().Equal("run-actor-1", "run-actor-2");
        dispatch.Dispatches.Select(x => x.Envelope.Id).Should().Equal(resolver.Requests[0].SessionId, resolver.Requests[1].SessionId);
        dispatch.Dispatches.Select(x => x.Envelope.Propagation?.CorrelationId)
            .Should().Equal(resolver.Requests[0].SessionId, resolver.Requests[1].SessionId);

        var firstChatRequest = dispatch.Dispatches[0].Envelope.Payload.Unpack<ChatRequestEvent>();
        firstChatRequest.SessionId.Should().Be(resolver.Requests[0].SessionId);
        firstChatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId].Should().Be(resolver.Requests[0].SessionId);
        firstChatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(resolver.Requests[0].SessionId);
        firstChatRequest.Metadata["x-trace"].Should().Be("trace-1");
    }

    [Fact]
    public async Task HandleFireAsync_ShouldPreserveWorkflowAdapterMetadataWithoutCoreWorkflowLeak()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            triggerEnvelope: CreateTriggerEnvelope("target-actor-1", new ChatRequestEvent
            {
                Prompt = "hello",
                Metadata =
                {
                    ["workflow.schedule_id"] = "schedule-1",
                },
            }),
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        var chatRequest = dispatch.Dispatches.Single().Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Metadata["workflow.schedule_id"].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldDispatchStoredEnvelopeToConfiguredNonWorkflowTarget()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "generic-agent-1",
            triggerEnvelope: CreateTriggerEnvelope("generic-agent-1", new ChatRequestEvent
            {
                Prompt = "generic scheduled prompt",
            }),
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("generic-agent-1");
        dispatched.Envelope.Route.GetTargetActorId().Should().Be("generic-agent-1");
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatched.Envelope.Id.Should().Be(idempotencyKey);
        var chatRequest = dispatched.Envelope.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        chatRequest.Metadata[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        chatRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.IdempotencyKey);
        agent.State.FireRecords[idempotencyKey].TargetActorId.Should().Be("generic-agent-1");
    }

    [Fact]
    public async Task HandleFireAsync_ShouldRejectUnsupportedPayloadToAvoidDroppingFireHeaders()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            triggerEnvelope: CreateTriggerEnvelope("target-actor-1", new Empty()),
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().ContainSingle()
            .Which.Value.Error.Should().Contain("does not support scheduled fire headers");
    }

    [Fact]
    public async Task HandleEventAsync_ShouldIgnoreStaleCallbackWithoutDispatchOrReschedule()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        var request = scheduler.TimeoutRequests.Single();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(request, generation: 99, fireIndex: 1));

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        scheduler.Canceled.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
        agent.State.NextFireLease!.Generation.Should().Be(1);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldIgnoreDisabledNonManualFireWithoutLeaseEnvelope()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = false,
        });

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        agent.State.FireCount.Should().Be(0);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ShouldRecordFailure_WhenDispatchIsNotAccepted()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            AdmissionFactory = (_, envelope) => new DispatchAdmission(
                Accepted: false,
                CommandId: envelope.Id,
                AckedAt: DateTimeOffset.UtcNow,
                ActorId: string.Empty,
                CorrelationId: envelope.Propagation?.CorrelationId ?? envelope.Id),
        };
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("Scheduled dispatch was not accepted.");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        agent.State.FireRecords[idempotencyKey].Error.Should().Be("Scheduled dispatch was not accepted.");
    }

    [Fact]
    public async Task HandleFireAsync_ShouldRecordFailure_WhenDispatchThrows()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            DispatchException = new InvalidOperationException("dispatch unavailable"),
        };
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatch.Dispatches.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("dispatch unavailable");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        agent.State.FireRecords[idempotencyKey].Error.Should().Be("dispatch unavailable");
    }

    [Fact]
    public async Task HandleFireAsync_WhenCanceled_ShouldNotRecordBusinessFailureOrScheduleNextFire()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            DispatchException = new OperationCanceledException("shutdown"),
        };
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        var act = () => agent.HandleFireAsync(
            new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
                Manual = false,
            },
            CreateFiredCallbackEnvelope(scheduler.TimeoutRequests.Single(), generation: 1, fireIndex: 1),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Started);
        agent.State.FireCount.Should().Be(0);
        agent.State.FailureCount.Should().Be(0);
        agent.State.LastError.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
    }

    [Fact]
    public void ScheduledDispatchFireRecordState_ShouldDefaultToUnspecifiedStatus()
    {
        new ScheduledDispatchFireRecordState()
            .Status.Should().Be(ScheduledDispatchFireStatusState.Unspecified);
    }

    [Fact]
    public void ScheduledDispatchState_ShouldNormalizeNullableTimestampsAndLeaseCodec()
    {
        var localTime = new DateTimeOffset(2026, 5, 29, 17, 0, 0, TimeSpan.FromHours(8));
        var state = new ScheduledDispatchState();

        state.CreatedAt.Should().Be(default);
        state.UpdatedAt.Should().Be(default);
        state.NextFireAt.Should().BeNull();
        state.LastFireAt.Should().BeNull();

        state.CreatedAt = localTime;
        state.UpdatedAt = localTime.AddMinutes(1);
        state.NextFireAt = localTime.AddMinutes(2);
        state.LastFireAt = localTime.AddMinutes(-2);

        state.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        state.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
        state.NextFireAt.Should().Be(localTime.AddMinutes(2).ToUniversalTime());
        state.LastFireAt.Should().Be(localTime.AddMinutes(-2).ToUniversalTime());

        state.NextFireAt = null;
        state.LastFireAt = null;
        state.NextFireAt.Should().BeNull();
        state.LastFireAt.Should().BeNull();

        ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(null).Should().BeNull();
        ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(null).Should().BeNull();
        ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(new ScheduledDispatchRuntimeCallbackLeaseState
        {
            ActorId = " ",
            CallbackId = "callback-1",
        }).Should().BeNull();
        ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(new ScheduledDispatchRuntimeCallbackLeaseState
        {
            ActorId = "actor-1",
            CallbackId = " ",
        }).Should().BeNull();

        var dedicated = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(
            new RuntimeCallbackLease("actor-1", "callback-1", 7, RuntimeCallbackBackend.Dedicated));
        dedicated.Should().NotBeNull();
        dedicated!.Backend.Should().Be(ScheduledDispatchRuntimeCallbackBackendState.Dedicated);

        var runtime = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(dedicated);
        runtime.Should().NotBeNull();
        runtime!.ActorId.Should().Be("actor-1");
        runtime.CallbackId.Should().Be("callback-1");
        runtime.Generation.Should().Be(7);
        runtime.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);

        var inMemory = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(
            new ScheduledDispatchRuntimeCallbackLeaseState
            {
                ActorId = "actor-2",
                CallbackId = "callback-2",
                Generation = 3,
                Backend = ScheduledDispatchRuntimeCallbackBackendState.InMemory,
            });
        inMemory.Should().NotBeNull();
        inMemory!.Backend.Should().Be(RuntimeCallbackBackend.InMemory);
    }

    private static ScheduledDispatchGAgent CreateAgent(
        IEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        RecordingRuntimeCallbackScheduler? callbackScheduler = null,
        IWorkflowRunActorResolver? workflowRunActorResolver = null,
        ICommandEnvelopeFactory<WorkflowChatRunRequest>? workflowChatEnvelopeFactory = null)
    {
        var agent = new ScheduledDispatchGAgent(dispatch, workflowRunActorResolver, workflowChatEnvelopeFactory)
        {
            Services = new TestServiceProvider(callbackScheduler),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScheduledDispatchState>(eventStore),
        };
        SetAgentId(agent, ScheduleActorId);
        return agent;
    }

    private static EventEnvelope CreateFiredCallbackEnvelope(
        RuntimeCallbackTimeoutRequest request,
        long generation,
        long fireIndex)
    {
        var envelope = request.TriggerEnvelope.Clone();
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        var callback = envelope.EnsureRuntime().EnsureCallback();
        callback.CallbackId = request.CallbackId;
        callback.Generation = generation;
        callback.FireIndex = fireIndex;
        callback.FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return envelope;
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private static ScheduledDispatchConfigureCommand CreateConfigureCommand(
        string scheduleId = "schedule-1",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null)
    {
        return new ScheduledDispatchConfigureCommand
        {
            ScheduleId = scheduleId,
            DisplayName = "Test schedule",
            TargetActorId = targetActorId,
            TriggerEnvelope = triggerEnvelope ?? CreateTriggerEnvelope(targetActorId, new ChatRequestEvent
            {
                Prompt = "hello",
                SessionId = "template-session",
            }),
            CronExpression = cronExpression,
            Timezone = "UTC",
            Enabled = enabled,
        };
    }

    private static EventEnvelope CreateTriggerEnvelope(string targetActorId, IMessage payload) =>
        new()
        {
            Id = "template-command",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("schedule-template", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "template-correlation",
            },
        };

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Func<string, EventEnvelope, DispatchAdmission> AdmissionFactory { get; set; } =
            DispatchAdmissionFactory.Create;

        public Exception? DispatchException { get; set; }

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            if (DispatchException != null)
                throw DispatchException;

            return Task.FromResult(AdmissionFactory(actorId, envelope));
        }
    }

    private sealed class RecordingWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        public List<WorkflowChatRunRequest> Requests { get; } = [];

        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new WorkflowActorResolutionResult(
                new WorkflowRunCreationReceipt(
                    $"run-actor-{Requests.Count}",
                    request.Source.ActorId ?? string.Empty,
                    []),
                request.Source.WorkflowName ?? string.Empty,
                WorkflowChatRunStartError.None));
        }
    }

    private sealed class RecordingWorkflowChatEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
    {
        public List<WorkflowChatRunRequest> Commands { get; } = [];
        public List<CommandContext> Contexts { get; } = [];

        public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
        {
            Commands.Add(command);
            Contexts.Add(context);

            var chatRequest = new ChatRequestEvent
            {
                Prompt = command.Prompt,
                SessionId = command.SessionId ?? context.CorrelationId,
                ScopeId = command.ScopeId ?? string.Empty,
            };
            foreach (var (key, value) in context.Headers)
                chatRequest.Headers[key] = value;
            chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId] = chatRequest.SessionId;
            foreach (var (key, value) in command.Metadata ?? new Dictionary<string, string>())
                chatRequest.Metadata[key] = value;

            return new EventEnvelope
            {
                Id = context.CommandId,
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(chatRequest),
                Route = EnvelopeRouteSemantics.CreateDirect("test", context.TargetId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = context.CorrelationId,
                },
            };
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly Dictionary<(string ActorId, string CallbackId), long> _generations = [];

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                DueTime = request.DueTime,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DeliveryMode = request.DeliveryMode,
            });

            var key = (request.ActorId, request.CallbackId);
            var generation = _generations.GetValueOrDefault(key) + 1;
            _generations[key] = generation;
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.Dedicated));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            throw new NotSupportedException("Scheduled dispatch tests only use one-shot durable timeouts.");
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestServiceProvider(RecordingRuntimeCallbackScheduler? callbackScheduler) : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return callbackScheduler;

            return null;
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);
        public bool ThrowOnAppend { get; set; }
        public string? ThrowOnAppendEventType { get; set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnAppend)
                throw new InvalidOperationException("append failed");
            if (!string.IsNullOrWhiteSpace(ThrowOnAppendEventType) &&
                events.Any(x => string.Equals(x.EventType, ThrowOnAppendEventType, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("append failed");
            }

            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events.Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(stream.Count == 0 ? 0 : stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0L);
        }
    }

}
