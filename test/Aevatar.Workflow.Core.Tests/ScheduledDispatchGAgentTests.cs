using Aevatar.AI.Abstractions;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Schedules;
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
    public async Task HandleFireAsync_ShouldSuppressDuplicateDispatchAfterTerminalRecordIsDurable()
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
    public async Task HandleFireAsync_WhenStartedRecordFromCanceledDispatchExists_ShouldRetry()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort
        {
            DispatchException = new OperationCanceledException("shutdown"),
        };
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        var canceled = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        await canceled.Should().ThrowAsync<OperationCanceledException>();
        dispatch.DispatchException = null;
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatch.Dispatches.Should().HaveCount(2);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
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
        agent.State.UpdatedAt.Should().Be(eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchNextFireScheduledEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<ScheduledDispatchNextFireScheduledEvent>())
            .Single()
            .ScheduledAt.ToDateTimeOffset());

        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.NextFireLease!.ActorId.Should().Be(ScheduleActorId);
        agent.State.NextFireLease.CallbackId.Should().Be(NextFireCallbackId);
        agent.State.NextFireLease.Generation.Should().Be(1);
        agent.State.NextFireLease.Backend.Should().Be(ScheduledDispatchRuntimeCallbackBackendState.Dedicated);
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenSchedulerFails_ShouldKeepPendingNextFireIntent()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("schedule failed"),
        };
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        var act = () => agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await act.Should().ThrowAsync<InvalidOperationException>();
        scheduler.TimeoutRequests.Should().ContainSingle();
        scheduler.Canceled.Should().BeEmpty();
        agent.State.PendingNextFireAt.Should().NotBeNull();
        agent.State.NextFireLease.Should().BeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchNextFireIntentRecordedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenReplacingNextFirePersistFails_ShouldKeepPreviousLeaseActiveAndCancelOnlyNewLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));
        var previousLease = agent.State.NextFireLease!.Clone();
        eventStore.ThrowOnAppendEventType = ScheduledDispatchNextFireScheduledEvent.Descriptor.FullName;

        var act = () => agent.HandleConfigureAsync(CreateUpdateCommand(
            cronExpression: "*/5 * * * *",
            enabled: true));

        await act.Should().ThrowAsync<InvalidOperationException>();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].Generation.Should().Be(2);
        agent.State.NextFireLease.Should().BeEquivalentTo(previousLease);
        agent.State.NextFireLease!.Generation.Should().Be(1);
        agent.State.PendingNextFireAt.Should().NotBeNull();
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
    public async Task HandleConfigureAsync_WhenEnabledUpdatePersistsNextFire_ShouldCancelPreviousLeaseAfterNewLeaseIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleConfigureAsync(CreateUpdateCommand(
            cronExpression: "*/5 * * * *",
            enabled: true));

        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Should().ContainSingle();
        scheduler.Canceled[0].Generation.Should().Be(1);
        agent.State.NextFireLease!.Generation.Should().Be(2);
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

        await agent.HandleConfigureAsync(CreateUpdateCommand(
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
    public async Task HandleConfigureAsync_ShouldRejectDuplicateCreateAndMissingUpdate()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();

        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var duplicateCreate = () => agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));
        await duplicateCreate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");

        var missingAgent = CreateAgent(new TestEventStore(), dispatch);
        await missingAgent.ActivateAsync();
        var missingUpdate = () => missingAgent.HandleConfigureAsync(CreateUpdateCommand(enabled: false));
        await missingUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is not configured*");
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
        chatRequest.SessionId.Should().Be("template-session");
        chatRequest.Metadata.Should().NotContainKey(ScheduledDispatchMetadataKeys.ScheduleId);
        dispatched.Envelope.Propagation!.Baggage[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.FireAtUtc]
            .Should().Be(firstScheduledFireAt.ToUniversalTime().ToString("O"));
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");

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
    public async Task HandleFireAsync_WithServiceInvocationRequest_ShouldDispatchTypedAdapterEnvelope()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(eventStore, dispatch, serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        var invocation = new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-1",
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
                ServiceId = "daily-workflow",
            },
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "run daily" }),
        };
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            triggerEnvelope: CreateTriggerEnvelope(
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                invocation),
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = invocation.Identity.Clone(),
                    EndpointId = invocation.EndpointId,
                    Payload = invocation.Payload.Clone(),
                },
            },
            enabled: false));

        var firstFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(firstFireAt),
            Manual = true,
        });

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", firstFireAt);
        dispatch.Dispatches.Should().BeEmpty();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        var serviceRequest = serviceInvocationDispatch.Requests.Single();
        serviceRequest.Identity.ServiceId.Should().Be("daily-workflow");
        serviceRequest.EndpointId.Should().Be("chat");
        serviceRequest.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("run daily");
        serviceRequest.CommandId.Should().Be(idempotencyKey);
        serviceRequest.CorrelationId.Should().Be(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].TargetActorId.Should().Be("service-run-actor");
        agent.State.FireRecords[idempotencyKey].CommandId.Should().Be(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].CorrelationId.Should().Be(idempotencyKey);
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
        chatRequest.Metadata.Should().NotContainKey(ScheduledDispatchMetadataKeys.ScheduleId);
        var baggage = dispatch.Dispatches.Single().Envelope.Propagation!.Baggage;
        baggage[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        baggage[ScheduledDispatchMetadataKeys.FireAtUtc].Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        baggage[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
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
        chatRequest.Metadata.Should().NotContainKey(ScheduledDispatchMetadataKeys.ScheduleId);
        dispatched.Envelope.Propagation!.Baggage[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.FireAtUtc]
            .Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        chatRequest.Metadata.Should().NotContainKey("workflow.schedule_id");
        chatRequest.Metadata.Should().NotContainKey("workflow.scheduled_fire_at_utc");
        agent.State.FireRecords[idempotencyKey].TargetActorId.Should().Be("generic-agent-1");
    }

    [Fact]
    public async Task HandleFireAsync_ShouldDispatchUnsupportedPayloadWithFireHeadersInBaggage()
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

        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches.Single();
        dispatched.ActorId.Should().Be("target-actor-1");
        dispatched.Envelope.Payload.Unpack<Empty>().Should().NotBeNull();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        dispatched.Envelope.Propagation!.Baggage[ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.FireAtUtc]
            .Should().Be(scheduledFireAt.ToUniversalTime().ToString("O"));
        dispatched.Envelope.Propagation.Baggage[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(idempotencyKey);
        agent.State.FireRecords.Should().ContainSingle()
            .Which.Value.TargetActorId.Should().Be("target-actor-1");
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
    public async Task HandleFireAsync_ForServiceInvocation_ShouldUseTypedTargetAndPropagateFireHeadersToChatPayload()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(eventStore, dispatch, serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            triggerEnvelope: CreateTriggerEnvelope("stale-target", new ServiceInvocationRequest
            {
                Identity = new ServiceIdentity { ServiceId = "stale-service" },
                EndpointId = "stale-endpoint",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "stale" }),
            }),
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent
                    {
                        Prompt = "configured",
                        Metadata = { ["caller"] = "kept" },
                    }),
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        dispatch.Dispatches.Should().BeEmpty();
        var request = serviceInvocationDispatch.Requests.Should().ContainSingle().Which;
        request.Identity.ServiceId.Should().Be("configured-service");
        request.EndpointId.Should().Be("chat");
        request.CommandId.Should().Be(ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt));
        request.CorrelationId.Should().Be(request.CommandId);
        var chatRequest = request.Payload.Unpack<ChatRequestEvent>();
        chatRequest.Prompt.Should().Be("configured");
        chatRequest.Metadata.Should().Contain("caller", "kept");
        chatRequest.Metadata.Should().NotContainKey(ScheduledDispatchMetadataKeys.ScheduleId);
        var headers = serviceInvocationDispatch.Headers.Should().ContainSingle().Which;
        headers.Should().NotBeNull();
        headers![ScheduledDispatchMetadataKeys.ScheduleId].Should().Be("schedule-1");
        headers.Should().ContainKey(ScheduledDispatchMetadataKeys.FireAtUtc);
        headers[ScheduledDispatchMetadataKeys.IdempotencyKey].Should().Be(request.CommandId);
    }

    [Fact]
    public async Task HandleFireAsync_ForServiceInvocationAuth_ShouldPassTypedAuthWithoutTokenExchange()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent
                    {
                        Prompt = "configured",
                        LlmControl = new LLMControlContextPayload
                        {
                            ModelOverride = "sonnet",
                        },
                    }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
                        {
                            Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = "lark",
                                Tenant = "tenant-1",
                                ExternalUserId = "ou-user-1",
                            },
                            Scope = "proxy",
                        },
                    },
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        var auth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().NotBeNull();
        auth.SenderNyxId!.Subject.ExternalUserId.Should().Be("ou-user-1");
        auth.SenderNyxId.Subject.Platform.Should().Be("lark");
        auth.SenderNyxId.Subject.Tenant.Should().Be("tenant-1");
        auth.SenderNyxId.Scope.Should().Be("proxy");
        var request = serviceInvocationDispatch.Requests.Should().ContainSingle().Which;
        var chatRequest = request.Payload.Unpack<ChatRequestEvent>();
        chatRequest.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        chatRequest.LlmControl.ModelOverride.Should().Be("sonnet");
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ForServiceInvocationAuthDispatchFailure_ShouldRecordFailure()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort
        {
            DispatchException = new InvalidOperationException("exchange failed"),
        };
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
                        {
                            Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = "lark",
                                Tenant = "tenant-1",
                                ExternalUserId = "ou-user-1",
                            },
                            Scope = "proxy",
                        },
                    },
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

        serviceInvocationDispatch.Auths.Should().ContainSingle();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("exchange failed");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
    }

    [Fact]
    public async Task HandleFireAsync_ForDuplicateServiceInvocationAuth_ShouldNotExchangeAgain()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
                        {
                            Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = "lark",
                                Tenant = "tenant-1",
                                ExternalUserId = "ou-user-1",
                            },
                            Scope = "proxy",
                        },
                    },
                },
            }));

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

        serviceInvocationDispatch.Auths.Should().ContainSingle();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
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
        var act = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
        });

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

    [Fact]
    public async Task HandleFireAsync_WhenPendingIntentHasNoLease_ShouldIgnoreCallback()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("schedule failed"),
        };
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        var failedConfigure = () => agent.HandleConfigureAsync(CreateConfigureCommand(enabled: true));
        await failedConfigure.Should().ThrowAsync<InvalidOperationException>();
        var pendingNextFireAt = agent.State.PendingNextFireAt!.Clone();
        var inbound = new EventEnvelope
        {
            Payload = Any.Pack(new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = pendingNextFireAt,
                Manual = false,
            }),
            Runtime = new EnvelopeRuntime
            {
                Callback = new EnvelopeCallbackContext
                {
                    CallbackId = NextFireCallbackId,
                    Generation = 1,
                    FireIndex = 1,
                    FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
        };

        var handleFire = typeof(ScheduledDispatchGAgent)
            .GetMethod("HandleFireAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        handleFire.Should().NotBeNull();
        var task = handleFire!.Invoke(agent,
        [
            new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = pendingNextFireAt,
                Manual = false,
            },
            inbound,
            CancellationToken.None,
        ]) as Task;
        task.Should().NotBeNull();
        await task!;

        dispatch.Dispatches.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchFireStartedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task OnActivateAsync_WhenPendingNextFireIntentExists_ShouldActivateLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("schedule failed"),
        };
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        var failedConfigure = () => agent.HandleConfigureAsync(CreateConfigureCommand(enabled: true));
        await failedConfigure.Should().ThrowAsync<InvalidOperationException>();
        var pendingNextFireAt = agent.State.PendingNextFireAt!.ToDateTimeOffset();
        scheduler.ScheduleException = null;

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        reactivated.State.PendingNextFireAt.Should().BeNull();
        reactivated.State.NextFireLease.Should().NotBeNull();
        reactivated.State.NextFireAt.Should().Be(pendingNextFireAt);
        scheduler.TimeoutRequests.Should().HaveCount(2);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchNextFireScheduledEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task OnActivateAsync_WhenPendingNextFireIntentHasPreviousLease_ShouldActivatePendingAndCancelPreviousLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));
        var previousLease = agent.State.NextFireLease!.Clone();
        eventStore.ThrowOnAppendEventType = ScheduledDispatchNextFireScheduledEvent.Descriptor.FullName;
        var failedUpdate = () => agent.HandleConfigureAsync(CreateUpdateCommand(
            cronExpression: "*/5 * * * *",
            enabled: true));
        await failedUpdate.Should().ThrowAsync<InvalidOperationException>();
        var pendingNextFireAt = agent.State.PendingNextFireAt!.ToDateTimeOffset();
        eventStore.ThrowOnAppendEventType = null;

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        reactivated.State.PendingNextFireAt.Should().BeNull();
        reactivated.State.NextFireLease.Should().NotBeNull();
        reactivated.State.NextFireAt.Should().Be(pendingNextFireAt);
        scheduler.Canceled.Should().Contain(x => x.Generation == previousLease.Generation);
    }

    [Fact]
    public void ScheduledDispatchStateReplay_ShouldUsePersistedNextFireScheduledAtForUpdatedAt()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        var scheduledAt = new DateTimeOffset(2026, 5, 29, 8, 59, 0, TimeSpan.Zero);
        var nextFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        var transition = typeof(ScheduledDispatchGAgent)
            .GetMethod("TransitionState", BindingFlags.Instance | BindingFlags.NonPublic);
        transition.Should().NotBeNull();

        var replayed = transition!.Invoke(agent,
            [
                new ScheduledDispatchState(),
                new ScheduledDispatchNextFireScheduledEvent
                {
                    NextFireAt = Timestamp.FromDateTimeOffset(nextFireAt),
                    ScheduledAt = Timestamp.FromDateTimeOffset(scheduledAt),
                    Lease = new ScheduledDispatchRuntimeCallbackLeaseState
                    {
                        ActorId = ScheduleActorId,
                        CallbackId = NextFireCallbackId,
                        Generation = 7,
                        Backend = ScheduledDispatchRuntimeCallbackBackendState.Dedicated,
                    },
                },
            ]) as ScheduledDispatchState;

        replayed.Should().NotBeNull();
        replayed!.NextFireAt.Should().Be(nextFireAt);
        replayed.UpdatedAt.Should().Be(scheduledAt);
        replayed.NextFireLease!.Generation.Should().Be(7);
    }

    private static ScheduledDispatchGAgent CreateAgent(
        IEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        RecordingRuntimeCallbackScheduler? callbackScheduler = null,
        RecordingScheduledServiceInvocationDispatchPort? serviceInvocationDispatch = null)
    {
        var agent = new ScheduledDispatchGAgent(
            dispatch,
            serviceInvocationDispatch ?? new RecordingScheduledServiceInvocationDispatchPort())
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

    private static ScheduledDispatchCreateCommand CreateConfigureCommand(
        string scheduleId = "schedule-1",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null,
        ScheduledDispatchTargetState? target = null)
    {
        return new ScheduledDispatchCreateCommand
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
            Target = target ?? CreateTargetState(targetActorId, triggerEnvelope),
        };
    }

    private static ScheduledDispatchUpdateCommand CreateUpdateCommand(
        string scheduleId = "schedule-1",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null,
        ScheduledDispatchTargetState? target = null)
    {
        return new ScheduledDispatchUpdateCommand
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
            Target = target ?? CreateTargetState(targetActorId, triggerEnvelope),
        };
    }

    private static ScheduledDispatchTargetState CreateTargetState(string targetActorId, EventEnvelope? triggerEnvelope) =>
        new()
        {
            Kind = ScheduledDispatchTargetKindState.Envelope,
            ActorId = targetActorId,
            Envelope = triggerEnvelope?.Clone(),
        };

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

    private sealed class RecordingScheduledServiceInvocationDispatchPort : IScheduledServiceInvocationDispatchPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];
        public List<ScheduledServiceInvocationAuth?> Auths { get; } = [];
        public List<IReadOnlyDictionary<string, string>?> Headers { get; } = [];

        public Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchReceipt> ReceiptFactory { get; set; } =
            dispatch => new ScheduledServiceInvocationDispatchReceipt(
                true,
                dispatch.Request.CommandId,
                "service-run-actor",
                dispatch.Request.CorrelationId);

        public Exception? DispatchException { get; set; }

        public Task<ScheduledServiceInvocationDispatchReceipt> DispatchAsync(
            ScheduledServiceInvocationDispatchRequest dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(dispatch.Request.Clone());
            Auths.Add(dispatch.Auth);
            Headers.Add(dispatch.Headers == null
                ? null
                : new Dictionary<string, string>(dispatch.Headers, StringComparer.Ordinal));
            if (DispatchException != null)
                throw DispatchException;

            return Task.FromResult(ReceiptFactory(dispatch));
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly Dictionary<(string ActorId, string CallbackId), long> _generations = [];

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Exception? ScheduleException { get; set; }

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
            if (ScheduleException != null)
                throw ScheduleException;

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

        public IReadOnlyList<StateEvent> GetEvents(string agentId) =>
            (_streams.GetValueOrDefault(agentId) ?? [])
            .Select(x => x.Clone())
            .ToArray();

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
