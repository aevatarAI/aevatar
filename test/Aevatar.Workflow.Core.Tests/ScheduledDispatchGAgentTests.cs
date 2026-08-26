using Aevatar.AI.Abstractions;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Workflow.Core.Tests;

public sealed class ScheduledDispatchGAgentTests
{
    private const string ScheduleActorId = "scheduled-dispatch:schedule-1";
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";
    private const string TeamCredentialExpiryCallbackId = "scheduled-dispatch-team-credential-expiry";
    private const string ManualFireIdempotencyKey = "manual-fire";
    private const string ExpectedServiceTargetMismatchError =
        "scheduled_dispatch_expected_service_target_mismatch";
    private const string LegacyUnmarkedEnvelopeRetiredError =
        "Scheduled dispatch envelope target is retired because it lacks trusted internal authority.";

    [Fact]
    public void AuthorizationFactState_ShouldReserveRemovedRuntimeNodeGrantTopology()
    {
        var file = FileDescriptorProto.Parser.ParseFrom(
            ScheduledInvocationAuthorizationFactState.Descriptor.File.SerializedData);
        var authorizationFact = file.MessageType.Single(message =>
            message.Name == nameof(ScheduledInvocationAuthorizationFactState));

        authorizationFact.Field.Should().NotContain(field =>
            field.Number == 10 || field.Name == "node_grants");
        authorizationFact.ReservedName.Should().Contain("node_grants");
        authorizationFact.ReservedRange.Should().Contain(range =>
            range.Start <= 10 && 10 < range.End);
        file.MessageType.Select(message => message.Name).Should()
            .NotContain("ScheduledInvocationAuthorizationNodeGrantState");

        var authority = file.MessageType.Single(message =>
            message.Name == nameof(ScheduledInvocationAuthorizationAuthorityState));
        authority.Field.Should().NotContain(field =>
            field.Number == 8 || field.Name == "catalog_external_revision");
        authority.ReservedName.Should().Contain("catalog_external_revision");
        authority.ReservedRange.Should().Contain(range =>
            range.Start <= 8 && 8 < range.End);
        authority.Field.Should().Contain(field =>
            field.Number == 10 && field.Name == "catalog_contract_version");
        authority.Field.Should().Contain(field =>
            field.Number == 11 && field.Name == "catalog_policy_version");
        authority.Field.Should().Contain(field =>
            field.Number == 12 && field.Name == "catalog_evaluated_at");
    }

    [Fact]
    public void EnvelopeAuthorityState_ShouldUseStableProtocolValuesAndFieldNumber()
    {
        ((int)ScheduledDispatchEnvelopeAuthorityState.Unspecified).Should().Be(0);
        ((int)ScheduledDispatchEnvelopeAuthorityState.TrustedInternal).Should().Be(1);
        ScheduledDispatchTargetState.Descriptor.Fields
            .InFieldNumberOrder()
            .Should()
            .ContainSingle(field =>
                field.FieldNumber == 7 && field.Name == "envelope_authority");
    }

    [Fact]
    public async Task HandleConfigureAsync_WithUnmarkedEnvelopeTarget_ShouldRequireTrustedInternalAuthority()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var command = CreateConfigureCommand(target: new ScheduledDispatchTargetState
        {
            Kind = ScheduledDispatchTargetKindState.Envelope,
            ActorId = "actor-cross-owner",
            Envelope = CreateTriggerEnvelope("actor-cross-owner", new Empty()),
        });

        var act = () => agent.HandleConfigureAsync(command);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*trusted internal authority*");
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleConfigureAsync_WithUnknownEnvelopeAuthority_ShouldRejectBeforePersistingEvents()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var command = CreateConfigureCommand(target: new ScheduledDispatchTargetState
        {
            Kind = ScheduledDispatchTargetKindState.Envelope,
            ActorId = "actor-unknown-authority",
            Envelope = CreateTriggerEnvelope("actor-unknown-authority", new Empty()),
            EnvelopeAuthority = (ScheduledDispatchEnvelopeAuthorityState)99,
        });

        var act = () => agent.HandleConfigureAsync(command);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*trusted internal authority*");
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleConfigureAsync_WithUnspecifiedTargetKind_ShouldRequireTypedTargetBeforePersistingEvents()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var command = CreateConfigureCommand(target: new ScheduledDispatchTargetState
        {
            Kind = ScheduledDispatchTargetKindState.Unspecified,
        });

        var act = () => agent.HandleConfigureAsync(command);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*typed target is required*");
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("ensure-create")]
    [InlineData("ensure-update")]
    public async Task ConfigureEntryPoints_WithMissingTypedTarget_ShouldRejectBeforePersistingEvents(
        string operation)
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        if (operation is "update" or "ensure-update")
            await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));

        var create = CreateConfigureCommand(enabled: false);
        create.Target = null;
        var update = CreateUpdateCommand(enabled: false);
        update.Target = null;
        var ensure = CreateEnsureCommand(enabled: false);
        ensure.Target = null;
        var eventCountBefore = eventStore.GetEvents(ScheduleActorId).Count;
        var configuredEventCountBefore = eventStore.GetEvents(ScheduleActorId)
            .Count(x => string.Equals(
                x.EventType,
                ScheduledDispatchConfiguredEvent.Descriptor.FullName,
                StringComparison.Ordinal));
        Func<Task> act = operation switch
        {
            "create" => () => agent.HandleConfigureAsync(create),
            "update" => () => agent.HandleConfigureAsync(update),
            "ensure-create" or "ensure-update" => () => agent.HandleEnsureAsync(ensure),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*typed target is required*");
        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCountBefore);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => string.Equals(
                x.EventType,
                ScheduledDispatchConfiguredEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should()
            .Be(configuredEventCountBefore);
    }

    [Fact]
    public async Task OnActivateAsync_WithEnabledLegacyUnmarkedEnvelopeSnapshot_ShouldRetireAndPurgeWithoutDispatch()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            actorDispatch,
            scheduler,
            serviceDispatch,
            snapshotStore: new TestSnapshotStore(
                CreateLegacyUnmarkedEnvelopeSnapshot(enabled: true),
                version: 0));

        await agent.ActivateAsync();

        agent.State.Enabled.Should().BeFalse();
        scheduler.PurgedActors.Should().ContainSingle().Which.Should().Be(ScheduleActorId);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(
                x.EventType,
                ScheduledDispatchDisabledEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should()
            .ContainSingle();
        scheduler.TimeoutRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task OnActivateAsync_WithDisabledLegacyUnmarkedEnvelopeSnapshot_ShouldPurgeWithoutPersistingOrScheduling()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            actorDispatch,
            scheduler,
            serviceDispatch,
            snapshotStore: new TestSnapshotStore(
                CreateLegacyUnmarkedEnvelopeSnapshot(enabled: false),
                version: 0));

        await agent.ActivateAsync();

        agent.State.Enabled.Should().BeFalse();
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
        scheduler.PurgedActors.Should().ContainSingle().Which.Should().Be(ScheduleActorId);
        scheduler.TimeoutRequests.Should().BeEmpty();
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleFireAsync_WithLegacyUnmarkedEnvelopeTarget_ShouldThrowRetirementErrorBeforeDispatch()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            actorDispatch,
            serviceInvocationDispatch: serviceDispatch,
            snapshotStore: new TestSnapshotStore(
                CreateLegacyUnmarkedEnvelopeSnapshot(enabled: false),
                version: 0));
        await agent.ActivateAsync();

        var act = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(LegacyUnmarkedEnvelopeRetiredError);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(
                x.EventType,
                ScheduledDispatchFireStartedEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task HandleFireAsync_AutomaticWithLegacyUnmarkedEnvelopeTarget_ShouldDisableAndPurgeWithoutDispatch()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            actorDispatch,
            scheduler,
            serviceDispatch,
            snapshotStore: new TestSnapshotStore(
                CreateLegacyUnmarkedEnvelopeSnapshot(enabled: true),
                version: 0));
        await agent.ActivateAsync();
        scheduler.PurgedActors.Clear();

        var command = new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = false,
        };

        await agent.HandleFireAsync(command);
        await agent.HandleFireAsync(command);

        agent.State.Enabled.Should().BeFalse();
        scheduler.PurgedActors.Should().HaveCount(2)
            .And.OnlyContain(actorId => actorId == ScheduleActorId);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(
                x.EventType,
                ScheduledDispatchFireStartedEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should()
            .BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => string.Equals(
                x.EventType,
                ScheduledDispatchDisabledEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should()
            .Be(1);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task ConditionalServiceTargetMutation_WhenExpectedTargetMismatches_ShouldRejectWithoutSideEffects(
        string operation)
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, actorDispatch, scheduler, serviceDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConditionalServiceTargetConfiguration(
            enabled: operation != "enable"));
        var stateBefore = agent.State.Clone();
        var eventCountBefore = eventStore.GetEvents(ScheduleActorId).Count;
        var timeoutCountBefore = scheduler.TimeoutRequests.Count;
        var canceledCountBefore = scheduler.Canceled.Count;
        var purgeCountBefore = scheduler.PurgedActors.Count;
        var mismatchedExpectedTarget = CreateExpectedServiceTarget("service-stale");
        var update = new ScheduledDispatchUpdateCommand
        {
            ScheduleId = agent.State.ScheduleId,
            DisplayName = "Conditionally updated schedule",
            TargetActorId = agent.State.TargetActorId,
            TriggerEnvelope = agent.State.TriggerEnvelope!.Clone(),
            CronExpression = "*/30 * * * *",
            Timezone = agent.State.Timezone,
            Enabled = false,
            Target = agent.State.Target!.Clone(),
            ScheduleKind = agent.State.ScheduleKind,
            ScheduleMode = agent.State.ScheduleMode,
            ExpectedServiceTarget = mismatchedExpectedTarget.Clone(),
        };
        Func<Task> act = operation switch
        {
            "update" => () => agent.HandleConfigureAsync(update),
            "enable" => () => agent.HandleEnableAsync(new ScheduledDispatchEnableCommand
            {
                Reason = "resume",
                ExpectedServiceTarget = mismatchedExpectedTarget.Clone(),
            }),
            "disable" => () => agent.HandleDisableAsync(new ScheduledDispatchDisableCommand
            {
                Reason = "pause",
                ExpectedServiceTarget = mismatchedExpectedTarget.Clone(),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(ExpectedServiceTargetMismatchError);

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCountBefore);
        agent.State.Equals(stateBefore).Should().BeTrue();
        scheduler.TimeoutRequests.Should().HaveCount(timeoutCountBefore);
        scheduler.Canceled.Should().HaveCount(canceledCountBefore);
        scheduler.PurgedActors.Should().HaveCount(purgeCountBefore);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleFireAsync_ManualWithMismatchedExpectedTarget_ShouldRejectBeforeLegacyEnvelopeRetirement()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            actorDispatch,
            scheduler,
            serviceDispatch,
            snapshotStore: new TestSnapshotStore(
                CreateLegacyUnmarkedEnvelopeSnapshot(enabled: false),
                version: 0));
        await agent.ActivateAsync();
        var stateBefore = agent.State.Clone();
        var eventCountBefore = eventStore.GetEvents(ScheduleActorId).Count;
        var canceledCountBefore = scheduler.Canceled.Count;
        var purgeCountBefore = scheduler.PurgedActors.Count;

        var act = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
            ExpectedServiceTarget = CreateExpectedServiceTarget("service-stale"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(ExpectedServiceTargetMismatchError);

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCountBefore);
        agent.State.Equals(stateBefore).Should().BeTrue();
        scheduler.Canceled.Should().HaveCount(canceledCountBefore);
        scheduler.PurgedActors.Should().HaveCount(purgeCountBefore);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteAsync_PartialReplayWithMismatchedExpectedTarget_ShouldRejectBeforeHealingOrPurge()
    {
        var eventStore = new TestEventStore();
        var seed = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await seed.ActivateAsync();
        await ActivateTeamAutomationAsync(
            seed,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await seed.HandleDeleteAsync(delete);
        eventStore.TruncateAfterEventType(
            ScheduleActorId,
            TeamAutomationDeletionRequestedEvent.Descriptor.FullName);

        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var reactivated = CreateAgent(
            eventStore,
            actorDispatch,
            scheduler,
            serviceDispatch);
        await reactivated.ActivateAsync();
        reactivated.State.TeamAutomationOperationKind.Should()
            .Be(TeamAutomationOperationKindState.Delete);
        reactivated.State.Deleted.Should().BeFalse();
        var stateBefore = reactivated.State.Clone();
        var eventCountBefore = eventStore.GetEvents(ScheduleActorId).Count;
        var timeoutCountBefore = scheduler.TimeoutRequests.Count;
        var canceledCountBefore = scheduler.Canceled.Count;
        var purgeCountBefore = scheduler.PurgedActors.Count;
        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-mismatched-target-replay";
        replay.ExpectedServiceTarget = CreateExpectedServiceTarget("service-stale");

        var act = () => reactivated.HandleDeleteAsync(replay);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(ExpectedServiceTargetMismatchError);

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCountBefore);
        reactivated.State.Equals(stateBefore).Should().BeTrue();
        reactivated.State.Deleted.Should().BeFalse();
        scheduler.TimeoutRequests.Should().HaveCount(timeoutCountBefore);
        scheduler.Canceled.Should().HaveCount(canceledCountBefore);
        scheduler.PurgedActors.Should().HaveCount(purgeCountBefore);
        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleFireAsync_AutomaticWithoutExpectedTarget_ShouldDispatchCurrentServiceTarget()
    {
        var eventStore = new TestEventStore();
        var actorDispatch = new RecordingActorDispatchPort();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, actorDispatch, scheduler, serviceDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConditionalServiceTargetConfiguration(enabled: true));
        var request = scheduler.TimeoutRequests.Single(x => x.CallbackId == NextFireCallbackId);
        var command = request.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        command.ExpectedServiceTarget.Should().BeNull();
        var scheduledFireAt = command.ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            request,
            generation: agent.State.NextFireLease!.Generation,
            fireIndex: 1,
            firedAt: scheduledFireAt));

        actorDispatch.Dispatches.Should().BeEmpty();
        serviceDispatch.Requests.Should().ContainSingle()
            .Which.Identity.ServiceId.Should().Be("service-alpha");
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey(
            "schedule-1",
            scheduledFireAt);
        agent.State.FireRecords[idempotencyKey].Status.Should()
            .Be(ScheduledDispatchFireStatusState.Dispatched);
    }

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
            IdempotencyKey = ManualFireIdempotencyKey,
        });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var idempotencyKey = ManualFireIdempotencyKey;
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        await canceled.Should().ThrowAsync<OperationCanceledException>();
        dispatch.DispatchException = null;
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var idempotencyKey = ManualFireIdempotencyKey;
        dispatch.Dispatches.Should().HaveCount(2);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_WhenNonManualCallbackDeliveredPastGrace_ShouldStillDispatch()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "*/15 * * * *", enabled: true));

        var request = scheduler.TimeoutRequests.Single();
        var scheduledFireAt = agent.State.NextFireAt!.Value;

        // The callback reaches the handler 20 minutes after its scheduled time (late delivery while
        // the grain stayed active). A late fire must still dispatch, not be suppressed, and is not
        // counted as an OnActivate overdue detection (that is a separate, reactivation-time signal).
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            request,
            generation: 1,
            fireIndex: 1,
            firedAt: scheduledFireAt.AddMinutes(20),
            scheduledFireAt: scheduledFireAt));

        dispatch.Dispatches.Should().ContainSingle();
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
        agent.State.OverdueFireDetectedCount.Should().Be(0);
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
    public async Task HandleConfigureAsync_WhenNextFireIsBeyondRuntimeRange_ShouldRegisterBoundedCallbackHop()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        await agent.HandleConfigureAsync(CreateConfigureCommand(
            cronExpression: CreateFarFutureCronExpression(),
            enabled: true));

        scheduler.TimeoutRequests.Should().ContainSingle();
        var request = scheduler.TimeoutRequests[0];
        request.DueTime.Should().Be(TimeSpan.FromDays(7));
        var fireCommand = request.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        var scheduledFireAt = fireCommand.ScheduledFireAt.ToDateTimeOffset();
        scheduledFireAt.Should().Be(agent.State.NextFireAt);
        scheduledFireAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenUnconfigured_ShouldCreateScheduleState()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();

        await agent.HandleEnsureAsync(CreateEnsureCommand(enabled: true));

        agent.State.ScheduleId.Should().Be("schedule-1");
        agent.State.Enabled.Should().BeTrue();
        agent.State.NextFireLease.Should().NotBeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchConfiguredEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenDefinitionIsIdentical_ShouldNoOpWithoutLeaseChurn()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        var command = CreateEnsureCommand(enabled: true);
        await agent.HandleEnsureAsync(command);
        var eventCount = eventStore.GetEvents(ScheduleActorId).Count;
        var lease = agent.State.NextFireLease!.Clone();

        await agent.HandleEnsureAsync(command);

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCount);
        scheduler.TimeoutRequests.Should().ContainSingle();
        scheduler.Canceled.Should().BeEmpty();
        agent.State.NextFireLease.Should().BeEquivalentTo(lease);
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenDefinitionChanges_ShouldUpdateAndCancelStaleLeaseAfterNewLeaseIsDurable()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleEnsureAsync(CreateEnsureCommand(cronExpression: "* * * * *", enabled: true));
        var previousLease = agent.State.NextFireLease!.Clone();

        await agent.HandleEnsureAsync(CreateEnsureCommand(
            targetActorId: "target-actor-updated",
            cronExpression: "*/5 * * * *",
            enabled: true));

        agent.State.TargetActorId.Should().Be("target-actor-updated");
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Should().ContainSingle()
            .Which.Generation.Should().Be(previousLease.Generation);
        agent.State.NextFireLease!.Generation.Should().Be(2);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchConfiguredEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task HandleFireAsync_WhenEnsureUpdateReplacesLease_ShouldIgnoreStaleCallback()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleEnsureAsync(CreateEnsureCommand(cronExpression: "* * * * *", enabled: true));
        var staleRequest = scheduler.TimeoutRequests.Single();
        await agent.HandleEnsureAsync(CreateEnsureCommand(cronExpression: "*/5 * * * *", enabled: true));

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(staleRequest, generation: 1, fireIndex: 1));

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        agent.State.NextFireLease!.Generation.Should().Be(2);
    }

    [Fact]
    public async Task HandleEnsureAsync_ShouldPreserveDuplicateFireSuppressionRecords()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleEnsureAsync(CreateEnsureCommand(enabled: false));
        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        await agent.HandleEnsureAsync(CreateEnsureCommand(displayName: "Updated schedule", enabled: false));
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var idempotencyKey = ManualFireIdempotencyKey;
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Dispatched);
    }

    [Fact]
    public async Task HandleConfigureAsync_WithTeamOwnerWithoutCredentialLifecycle_ShouldRegisterNextFireLease()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), scheduler);
        await agent.ActivateAsync();
        var command = CreateConfigureCommand(cronExpression: "* * * * *", enabled: true);
        command.TeamAutomationOwner = CreateTeamOwner();

        await agent.HandleConfigureAsync(command);

        agent.State.TeamAutomationOwner.Should().NotBeNull();
        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Unspecified);
        agent.State.NextFireAt.Should().NotBeNull();
        agent.State.NextFireLease.Should().NotBeNull();
        scheduler.TimeoutRequests.Should().ContainSingle()
            .Which.CallbackId.Should().Be(NextFireCallbackId);
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
    public async Task HandleEnableAsync_AfterDisable_ShouldRegisterNewNextFireLease()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleDisableAsync(new ScheduledDispatchDisableCommand { Reason = "pause" });
        await agent.HandleEnableAsync(new ScheduledDispatchEnableCommand { Reason = "resume" });

        agent.State.Enabled.Should().BeTrue();
        agent.State.NextFireAt.Should().NotBeNull();
        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.NextFireLease!.Generation.Should().Be(2);
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Should().ContainSingle()
            .Which.Generation.Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchEnabledEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which.EventData.Unpack<ScheduledDispatchEnabledEvent>()
            .Should().Match<ScheduledDispatchEnabledEvent>(evt =>
                evt.Reason == "resume" && evt.ScheduleId == "schedule-1");
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
    public async Task HandleConfigureAsync_WhenUpdateSuppliesFreshAuthorizationFact_ShouldPreserveExistingAuth()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-active");
        await ActivateTeamAutomationAsync(agent, credential, enabled: false);
        var updatedTarget = agent.State.Target!.Clone();
        updatedTarget.ServiceInvocation.Auth = null;
        updatedTarget.ServiceInvocation.AuthorizationFact.PermissionDigest = "digest-updated";
        updatedTarget.ServiceInvocation.AuthorizationFact.OwnerLlmSelection = CreateOwnerLLMSelection();
        var update = CreateUpdateCommand(
            displayName: "Updated schedule",
            target: updatedTarget,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow);
        update.TeamAutomationOwner = owner.Clone();

        await agent.HandleConfigureAsync(update);

        agent.State.Target!.ServiceInvocation!.Auth!.ScheduledInvocationAgentKey!.ApiKeyId
            .Should().Be("key-active");
        agent.State.Target.ServiceInvocation.AuthorizationFact.PermissionDigest.Should().Be("digest-updated");
        agent.State.Target.ServiceInvocation.AuthorizationFact.OwnerLlmSelection
            .Should().BeEquivalentTo(CreateOwnerLLMSelection());
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
    public async Task HandleConfigureAsync_WhenRequiredWorkflowTargetMissingCredentials_ShouldRejectWithoutStateMutation()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        var previousState = agent.State.Clone();

        var rejected = () => agent.HandleConfigureAsync(CreateConfigureCommand(
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
            target: CreateWorkflowServiceInvocationTarget()));

        await rejected.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a typed service invocation credential source*");
        agent.State.ToByteArray().Should().Equal(previousState.ToByteArray());
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Theory]
    [InlineData("connector.http.authorization")]
    [InlineData("Connector.Http.Authorization")]
    [InlineData("CONNECTOR.HTTP.AUTHORIZATION")]
    public async Task HandleEnsureAsync_WhenCurrentSessionCredentialHeaderIsPresent_ShouldRejectWithoutStateMutation(
        string authorizationHeader)
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        var previousState = agent.State.Clone();
        var command = CreateEnsureCommand(
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
            target: CreateWorkflowServiceInvocationTarget(CreateSenderNyxIdAuth()));
        command.Headers[authorizationHeader] = "redacted";

        var rejected = () => agent.HandleEnsureAsync(command);

        await rejected.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*current-session credentials*");
        agent.State.ToByteArray().Should().Equal(previousState.ToByteArray());
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleConfigureAsync_WhenUpdateUsesLegacyDurableBearer_ShouldRejectWithoutStateMutation()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
            target: CreateWorkflowServiceInvocationTarget(CreateSenderNyxIdAuth())));
        var previousState = agent.State.Clone();
        var previousEventCount = eventStore.GetEvents(ScheduleActorId).Count;

        var rejected = () => agent.HandleConfigureAsync(CreateUpdateCommand(
            displayName: "Rejected legacy auth update",
            target: CreateWorkflowServiceInvocationTarget(CreateLegacyDurableBearerAuth())));

        await rejected.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*credential source is not supported*");
        agent.State.ToByteArray().Should().Equal(previousState.ToByteArray());
        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(previousEventCount);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchConfiguredEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task HandleDeleteAsync_WhenConfiguredEnabled_ShouldPersistDeletedStateAndPurgeCallbacks()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
        {
            Reason = "remove",
        });

        agent.State.Deleted.Should().BeTrue();
        agent.State.DeletedAt.Should().NotBeNull();
        agent.State.Enabled.Should().BeFalse();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        agent.State.PendingNextFireAt.Should().BeNull();
        scheduler.PurgedActors.Should().ContainSingle()
            .Which.Should().Be(ScheduleActorId);
        scheduler.Canceled.Should().BeEmpty();
        var deleted = eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchDeletedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which.EventData.Unpack<ScheduledDispatchDeletedEvent>();
        deleted.Reason.Should().Be("remove");
        deleted.ScheduleId.Should().Be("schedule-1");
    }

    [Fact]
    public async Task DeletedSchedule_ShouldRejectMutationsAndManualFireWithoutDispatch()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false));
        await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand());

        var enable = () => agent.HandleEnableAsync(new ScheduledDispatchEnableCommand());
        var disable = () => agent.HandleDisableAsync(new ScheduledDispatchDisableCommand());
        var update = () => agent.HandleConfigureAsync(CreateUpdateCommand(enabled: false));
        var manualFire = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        await enable.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
        await disable.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
        await update.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deleted*");
        await manualFire.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
        dispatch.Dispatches.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchFireStartedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task HandleEventAsync_WhenLateNonManualCallbackArrivesAfterDelete_ShouldIgnoreWithoutDispatchOrFireStartedEvent()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));
        var request = scheduler.TimeoutRequests.Single();
        await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand());

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(request, generation: 1, fireIndex: 1));

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchFireStartedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .BeEmpty();
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

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            firstRequest,
            generation: 1,
            fireIndex: 1,
            firedAt: firstScheduledFireAt));

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
    public async Task HandleEventAsync_WhenBoundedCallbackArrivesEarly_ShouldRearmWithoutDispatching()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            cronExpression: CreateFarFutureCronExpression(),
            enabled: true));
        var firstRequest = scheduler.TimeoutRequests.Single();
        var firstFireCommand = firstRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        var scheduledFireAt = firstFireCommand.ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(firstRequest, generation: 1, fireIndex: 1));

        dispatch.Dispatches.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        agent.State.FireCount.Should().Be(0);
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.TimeoutRequests[1].DueTime.Should().Be(TimeSpan.FromDays(7));
        var rearmedFireCommand = scheduler.TimeoutRequests[1].TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        rearmedFireCommand.ScheduledFireAt.ToDateTimeOffset().Should().Be(scheduledFireAt);
        agent.State.NextFireAt.Should().Be(scheduledFireAt);
        agent.State.NextFireLease!.Generation.Should().Be(2);
        scheduler.Canceled.Should().ContainSingle()
            .Which.Generation.Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchFireStartedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task HandleEventAsync_WhenOneShotCallbackDispatches_ShouldCompleteWithoutSchedulingAnotherFire()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        var fireAt = DateTimeOffset.UtcNow.AddHours(1);
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            cronExpression: string.Empty,
            enabled: true,
            scheduleMode: ScheduledDispatchScheduleModeState.OneShotAtUtc,
            oneShotFireAt: fireAt));
        var request = scheduler.TimeoutRequests.Should().ContainSingle().Which;

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(request, generation: 1, fireIndex: 1, firedAt: fireAt.AddMilliseconds(1)));

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", fireAt);
        dispatch.Dispatches.Should().ContainSingle();
        agent.State.FireRecords.Should().ContainKey(idempotencyKey);
        agent.State.Completed.Should().BeTrue();
        agent.State.CompletedAt.Should().NotBeNull();
        agent.State.Enabled.Should().BeFalse();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        scheduler.TimeoutRequests.Should().ContainSingle();
        scheduler.Canceled.Should().ContainSingle()
            .Which.Generation.Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchCompletedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var idempotencyKey = ManualFireIdempotencyKey;
        dispatch.Dispatches.Should().BeEmpty();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        var serviceRequest = serviceInvocationDispatch.Requests.Single();
        serviceRequest.Identity.ServiceId.Should().Be("daily-workflow");
        serviceRequest.EndpointId.Should().Be("chat");
        serviceRequest.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("run daily");
        serviceRequest.CommandId.Should().Be(idempotencyKey);
        serviceRequest.CorrelationId.Should().Be(idempotencyKey);
        serviceInvocationDispatch.FireContexts.Should().ContainSingle().Which.Should().Be(
            new ScheduledDispatchFireContext(firstFireAt, "UTC"));
        agent.State.FireRecords[idempotencyKey].TargetActorId.Should().Be("service-run-actor");
        agent.State.FireRecords[idempotencyKey].CommandId.Should().Be(idempotencyKey);
        agent.State.FireRecords[idempotencyKey].CorrelationId.Should().Be(idempotencyKey);
    }

    [Fact]
    public async Task HandleConfigureAsync_ShouldRejectUnknownScheduledPromptPlaceholderBeforePersisting()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var target = CreateWorkflowServiceInvocationTarget(payload: new ChatRequestEvent
        {
            Prompt = "{\"run_date\":\"{{@schedule.unknown}}\"}",
        });

        var act = () => agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            triggerEnvelope: CreateTriggerEnvelope(
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                new ServiceInvocationRequest
                {
                    Payload = target.ServiceInvocation!.Payload.Clone(),
                }),
            target: target,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported scheduled prompt placeholder '@schedule.unknown'.*");
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleConfigureAsync_ForServiceInvocation_ShouldStripScheduleOwnedCredentialsFromPersistedPayloads()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
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
            Payload = Any.Pack(CreateCredentialBearingChatRequest("trigger")),
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
                    Payload = Any.Pack(CreateCredentialBearingChatRequest("target")),
                },
            },
            enabled: false));

        var configuredEvent = eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchConfiguredEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which
            .EventData
            .Unpack<ScheduledDispatchConfiguredEvent>();
        var persistedTriggerChat = configuredEvent.TriggerEnvelope.Payload
            .Unpack<ServiceInvocationRequest>()
            .Payload
            .Unpack<ChatRequestEvent>();
        var persistedTargetChat = configuredEvent.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>();
        var stateTriggerChat = agent.State.TriggerEnvelope!.Payload
            .Unpack<ServiceInvocationRequest>()
            .Payload
            .Unpack<ChatRequestEvent>();
        var stateTargetChat = agent.State.Target!.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();

        AssertScheduleOwnedCredentialFieldsStripped(persistedTriggerChat, "trigger");
        AssertScheduleOwnedCredentialFieldsStripped(persistedTargetChat, "target");
        AssertScheduleOwnedCredentialFieldsStripped(stateTriggerChat, "trigger");
        AssertScheduleOwnedCredentialFieldsStripped(stateTargetChat, "target");
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var idempotencyKey = ManualFireIdempotencyKey;
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
    public async Task HandleFireAsync_WithTrustedInternalEnvelope_ShouldDispatchStoredEnvelopeToConfiguredNonWorkflowTarget()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        var triggerEnvelope = CreateTriggerEnvelope("generic-agent-1", new ChatRequestEvent
        {
            Prompt = "generic scheduled prompt",
        });
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            targetActorId: "generic-agent-1",
            triggerEnvelope: triggerEnvelope,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = "generic-agent-1",
                Envelope = triggerEnvelope.Clone(),
                EnvelopeAuthority = ScheduledDispatchEnvelopeAuthorityState.TrustedInternal,
            },
            enabled: false));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 10, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches[0];
        dispatched.ActorId.Should().Be("generic-agent-1");
        dispatched.Envelope.Route.GetTargetActorId().Should().Be("generic-agent-1");
        var idempotencyKey = ManualFireIdempotencyKey;
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
        agent.State.Target!.EnvelopeAuthority.Should().Be(
            ScheduledDispatchEnvelopeAuthorityState.TrustedInternal);
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        dispatch.Dispatches.Should().ContainSingle();
        var dispatched = dispatch.Dispatches.Single();
        dispatched.ActorId.Should().Be("target-actor-1");
        dispatched.Envelope.Payload.Unpack<Empty>().Should().NotBeNull();
        var idempotencyKey = ManualFireIdempotencyKey;
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        dispatch.Dispatches.Should().BeEmpty();
        var request = serviceInvocationDispatch.Requests.Should().ContainSingle().Which;
        request.Identity.ServiceId.Should().Be("configured-service");
        request.EndpointId.Should().Be("chat");
        request.CommandId.Should().Be(ManualFireIdempotencyKey);
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
            IdempotencyKey = ManualFireIdempotencyKey,
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
        chatRequest.ConnectorHttpAuthorization.Should().BeEmpty();
        chatRequest.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        chatRequest.LlmControl.ModelOverride.Should().Be("sonnet");
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ForServiceInvocation_ShouldStripConnectorAuthorizationScheduleHeader()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        var command = CreateConfigureCommand(
            enabled: false,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                },
            });
        command.Headers[ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer stored-header-token";
        command.Headers["trace"] = "kept";
        await agent.HandleConfigureAsync(command);

        agent.State.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        agent.State.Headers.Should().Contain("trace", "kept");

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var headers = serviceInvocationDispatch.Headers.Should().ContainSingle().Which;
        headers.Should().NotBeNull();
        headers!.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        headers.Should().Contain("trace", "kept");
        headers.Should().ContainKey(ScheduledDispatchMetadataKeys.ScheduleId);
    }

    [Fact]
    public async Task HandleFireAsync_ForScopeOwnerServiceInvocationAuth_ShouldPassScopeOwnerAuthWithoutSenderSubject()
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
                        ConnectorHttpAuthorization = "Bearer stale-schedule-token",
                    }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState
                        {
                            Scope = " owner-proxy ",
                            OwnerSubject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = OwnerScope.NyxIdPlatform,
                                Tenant = string.Empty,
                                ExternalUserId = " owner-nyx-user ",
                            },
                        },
                    },
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var auth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().BeNull();
        auth.ScopeOwnerNyxId!.Scope.Should().Be("owner-proxy");
        auth.ScopeOwnerNyxId.OwnerSubject.Should().BeEquivalentTo(new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            "owner-nyx-user"));
        serviceInvocationDispatch.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<ChatRequestEvent>().ConnectorHttpAuthorization.Should().BeEmpty();
        agent.State.Target!.ServiceInvocation!.Auth!.ScopeOwnerNyxId.Should().BeNull();
        agent.State.Target.ServiceInvocation.Auth.NyxId!.Role.Should()
            .Be(ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner);
        agent.State.Target.ServiceInvocation.Auth.NyxId.Scope.Should().Be("owner-proxy");
        agent.State.Target.ServiceInvocation.Auth.NyxId.Subject.ExternalUserId.Should().Be("owner-nyx-user");
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ForDurableCredentialReferenceAuth_ShouldPassReferenceWithoutResolvingSecret()
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
                    }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        Durable = new ScheduledServiceInvocationDurableCredentialReferenceState
                        {
                            CredentialId = "credential-1",
                            SecretReference = new SecretReference
                            {
                                Ref = "sec-1",
                                Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                                OwnerScopeKey = "owner-scope-1",
                            },
                        },
                    },
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var auth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
        auth.Durable.Should().NotBeNull();
        auth.Durable!.CredentialId.Should().Be("credential-1");
        auth.Durable.SecretReference.Ref.Should().Be("sec-1");
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        agent.State.Target!.ServiceInvocation!.Auth!.Durable!.CredentialId.Should().Be("credential-1");
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenServiceInvocationNoOpOmitsAuth_ShouldPreserveExistingAuthWithoutConfiguredEvent()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        var triggerEnvelope = CreateTriggerEnvelope("target-actor-1", new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "template-session",
        });
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            triggerEnvelope: triggerEnvelope,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
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
                        ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState
                        {
                            Scope = "proxy",
                            OwnerSubject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = OwnerScope.NyxIdPlatform,
                                Tenant = string.Empty,
                                ExternalUserId = "owner-nyx-user",
                            },
                        },
                    },
                },
            }));
        var eventCount = eventStore.GetEvents(ScheduleActorId).Count;

        await agent.HandleEnsureAsync(CreateEnsureCommand(
            triggerEnvelope: triggerEnvelope,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                },
            }));

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCount);
        agent.State.Target!.ServiceInvocation!.Auth.Should().NotBeNull();
        agent.State.Target.ServiceInvocation.Auth!.NyxId!.Role.Should()
            .Be(ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner);
        agent.State.Target.ServiceInvocation.Auth.NyxId.Scope.Should().Be("proxy");
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenServiceInvocationUpdateOmitsAuth_ShouldPreserveExistingAuth()
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
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
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
                        ScopeOwnerNyxId = new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState
                        {
                            Scope = "proxy",
                            OwnerSubject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = OwnerScope.NyxIdPlatform,
                                Tenant = string.Empty,
                                ExternalUserId = "owner-nyx-user",
                            },
                        },
                    },
                },
            }));

        await agent.HandleEnsureAsync(CreateEnsureCommand(
            displayName: "Updated schedule",
            targetActorId: ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "configured-service" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "updated" }),
                },
            }));
        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        agent.State.Target!.ServiceInvocation!.Auth.Should().NotBeNull();
        agent.State.Target.ServiceInvocation.Auth!.NyxId!.Role.Should()
            .Be(ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner);
        agent.State.Target.ServiceInvocation.Auth.NyxId.Scope.Should().Be("proxy");
        var auth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        auth.Should().NotBeNull();
        auth!.ScopeOwnerNyxId!.Scope.Should().Be("proxy");
        auth.ScopeOwnerNyxId.OwnerSubject!.ExternalUserId.Should().Be("owner-nyx-user");
    }

    [Fact]
    public async Task HandleFireAsync_ForWorkflowServiceInvocationAuth_ShouldNotRequestWorkflowCallerCredentialProjection()
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
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        serviceInvocationDispatch.Auths.Should().ContainSingle()
            .Which!.SenderNyxId!.Subject.ExternalUserId.Should().Be("ou-user-1");
        serviceInvocationDispatch.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<ChatRequestEvent>().ConnectorHttpAuthorization.Should().BeEmpty();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_ForScheduledInvocationAgentKeyAuth_ShouldPassReferenceAndRequestWorkflowProjection()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        var createdAtUnixMs = DateTimeOffset.Parse("2026-06-18T00:00:00+00:00")
            .ToUnixTimeMilliseconds();
        var expiresAtUnixMs = DateTimeOffset.Parse("2026-07-18T00:00:00+00:00").ToUnixTimeMilliseconds();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
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
                    }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        ScheduledInvocationAgentKey = new ScheduledInvocationAgentKeyCredentialReferenceState
                        {
                            SecretReference = new SecretReference
                            {
                                Ref = "sec-schedule",
                                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                                OwnerScopeKey = "scope-key",
                                Fingerprint = "sha256:abc",
                                Version = 7,
                                CreatedAtUnixMs = createdAtUnixMs,
                                ExpiresAtUnixMs = expiresAtUnixMs,
                            },
                            ApiKeyId = "key-schedule",
                            KeyExpiresAtUnixMs = expiresAtUnixMs,
                            NyxIdDurableOperationGrants =
                            {
                                new NyxIdDurableOperationGrantRef
                                {
                                    GrantId = "grant-executions",
                                    ApiKeyId = "key-schedule",
                                    UserServiceId = "us-code-alpha",
                                    EndpointId = "endpoint-executions",
                                    HttpMethod = NyxIdDurableOperationHttpMethod.Post,
                                    NormalizedPathTemplate = "/executions",
                                    ContractDigest =
                                        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                                    ValidFromUnixMs = createdAtUnixMs,
                                    ExpiresAtUnixMs = expiresAtUnixMs,
                                    ReplayPolicy =
                                        NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey,
                                },
                            },
                        },
                    },
                },
            }));

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var auth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        auth.Should().NotBeNull();
        auth!.SenderNyxId.Should().BeNull();
        auth.ScopeOwnerNyxId.Should().BeNull();
        auth.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey!.ApiKeyId.Should().Be("key-schedule");
        auth.ScheduledInvocationAgentKey.KeyExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
        auth.ScheduledInvocationAgentKey.SecretReference.Ref.Should().Be("sec-schedule");
        auth.ScheduledInvocationAgentKey.SecretReference.Purpose.Should()
            .Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        auth.ScheduledInvocationAgentKey.SecretReference.OwnerScopeKey.Should().Be("scope-key");
        auth.ScheduledInvocationAgentKey.SecretReference.Fingerprint.Should().Be("sha256:abc");
        auth.ScheduledInvocationAgentKey.SecretReference.Version.Should().Be(7);
        auth.ScheduledInvocationAgentKey.SecretReference.ExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
        var durableGrant = auth.ScheduledInvocationAgentKey.DurableOperationGrants
            .Should().ContainSingle().Which;
        durableGrant.GrantId.Should().Be("grant-executions");
        durableGrant.ApiKeyId.Should().Be("key-schedule");
        durableGrant.UserServiceId.Should().Be("us-code-alpha");
        durableGrant.NormalizedPathTemplate.Should().Be("/executions");
        serviceInvocationDispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredentials.Should()
            .ContainSingle()
            .Which.Should().BeTrue();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleFireAsync_WithAuthorizationFact_ShouldPreserveEveryPermissionField()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(eventStore, dispatch, serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        var observedAt = new DateTimeOffset(2026, 7, 15, 7, 0, 0, TimeSpan.Zero);
        var freshUntil = observedAt.AddHours(2);
        var evaluatedAt = observedAt.AddMinutes(-5);
        var expiresAt = observedAt.AddHours(1);
        var authorizationFact = new ScheduledInvocationAuthorizationFactState
        {
            PermissionDigest = "digest-alpha",
            PolicyVersion = "policy-v1",
            Owner = new ScheduledInvocationAuthorizationOwnerState
            {
                Authority = "nyxid",
                OwnerKind = "personal",
                OwnerSubject = "owner-alpha",
            },
            Scopes = "proxy chat",
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            ServiceGrantsNotRequired = false,
            Disclosure = new ScheduledInvocationAuthorizationDisclosureState
            {
                DedicatedToSchedule = true,
                SecretManagedByAevatar = true,
                BrowserReceivesRawKey = false,
                DeleteRevokesCredential = true,
                PauseResumeRevokesCredential = true,
            },
            Authority = new ScheduledInvocationAuthorizationAuthorityState
            {
                MemberStateVersion = 11,
                WorkflowStateVersion = 12,
                ConnectorStateVersion = 13,
                OwnerLlmStateVersion = 14,
                CatalogStateVersion = 15,
                CatalogObservedAt = Timestamp.FromDateTimeOffset(observedAt),
                CatalogFreshUntil = Timestamp.FromDateTimeOffset(freshUntil),
                CatalogContentDigest = "catalog-digest-alpha",
                CatalogContractVersion = "scope-plan-contract/v1",
                CatalogPolicyVersion = "scope-plan-policy/v1",
                CatalogEvaluatedAt = Timestamp.FromDateTimeOffset(evaluatedAt),
            },
            OwnerLlmSelection = new ScheduledInvocationOwnerLLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                NyxIdUserServiceId = "nyx-llm-service-alpha",
                ServiceSlugSnapshot = "chrono-llm-public",
                Model = "gpt-5.5",
            },
        };
        authorizationFact.ServiceGrants.Add(new ScheduledInvocationAuthorizationServiceGrantState
        {
            ServiceId = "svc-alpha",
            NodeIds = { "node-alpha", "node-beta" },
            NodeGrantsNotRequired = false,
        });
        var target = new ScheduledDispatchTargetState
        {
            Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
            ServiceInvocation = new ScheduledServiceInvocationTargetState
            {
                Identity = new ServiceIdentity { ServiceId = "svc-alpha" },
                EndpointId = "chat",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                AuthorizationFact = authorizationFact,
            },
        };

        await agent.HandleConfigureAsync(CreateConfigureCommand(enabled: false, target: target));

        agent.State.Target!.ServiceInvocation!.AuthorizationFact.Should().BeEquivalentTo(authorizationFact);

        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(5)),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var dispatchedFact = serviceInvocationDispatch.AuthorizationFacts.Should().ContainSingle().Which;
        dispatchedFact.Should().NotBeNull();
        dispatchedFact!.PermissionDigest.Should().Be("digest-alpha");
        dispatchedFact.PolicyVersion.Should().Be("policy-v1");
        dispatchedFact.Owner.Should().Be(new ScheduledInvocationAuthorizationOwner("nyxid", "personal", "owner-alpha"));
        dispatchedFact.ServiceGrants.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ScheduledInvocationAuthorizationServiceGrant("svc-alpha", ["node-alpha", "node-beta"], false));
        dispatchedFact.Scopes.Should().Be("proxy chat");
        dispatchedFact.ExpiresAt.Should().Be(expiresAt);
        dispatchedFact.ServiceGrantsNotRequired.Should().BeFalse();
        dispatchedFact.Disclosure.Should().Be(
            new ScheduledInvocationAuthorizationDisclosure(true, true, false, true, true));
        dispatchedFact.Authority.Should().Be(new ScheduledInvocationAuthorizationAuthority(
            11,
            12,
            13,
            14,
            15,
            observedAt,
            freshUntil,
            "catalog-digest-alpha",
            "scope-plan-contract/v1",
            "scope-plan-policy/v1",
            evaluatedAt));
        dispatchedFact.OwnerLLMSelection.Should().BeEquivalentTo(authorizationFact.OwnerLlmSelection);
        dispatchedFact.OwnerLLMSelection.Should().NotBeSameAs(authorizationFact.OwnerLlmSelection);
    }

    [Fact]
    public async Task HandleFireAsync_ForLegacyDurableBearerTokenAuth_ShouldFailClosed()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        agent.State.ScheduleId = "schedule-1";
        agent.State.CronExpression = "0 9 * * *";
        agent.State.Timezone = "UTC";
        agent.State.ScheduleKind = ScheduledDispatchScheduleKindState.Workflow;
        agent.State.TriggerEnvelope = new EventEnvelope { Payload = Any.Pack(new ServiceInvocationRequest()) };
        agent.State.Target = new ScheduledDispatchTargetState
        {
            Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService,
            ServiceInvocation = new ScheduledServiceInvocationTargetState
            {
                Identity = new ServiceIdentity { ServiceId = "configured-service" },
                EndpointId = "chat",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                Auth = new ScheduledServiceInvocationAuthState
                {
                    DurableSenderBearerToken = "durable-run-key",
                },
            },
        };

        var stateAuth = agent.State.Target.ServiceInvocation!.Auth!;
        stateAuth.DurableSenderBearerToken.Should().Be("durable-run-key");
        stateAuth.LegacyDurableSenderBearerBlocked.Should().BeFalse();

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        serviceInvocationDispatch.Auths.Should().BeEmpty();
        serviceInvocationDispatch.Requests.Should().BeEmpty();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Contain("legacy durable bearer auth");
    }

    [Fact]
    public async Task HandleFireAsync_ForDurableCredentialReferenceAuth_ShouldPassReferenceToDispatchAndRecordFailure()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort
        {
            DispatchException = new InvalidOperationException(
                "Scheduled service invocation durable credential reference exchange is not available in this phase."),
        };
        var agent = CreateAgent(
            eventStore,
            dispatch,
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
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
                        Durable = new ScheduledServiceInvocationDurableCredentialReferenceState
                        {
                            CredentialId = " durable-run-key ",
                        },
                    },
                },
            }));

        var stateAuth = agent.State.Target!.ServiceInvocation!.Auth!;
        stateAuth.Durable.Should().NotBeNull();
        stateAuth.Durable!.CredentialId.Should().Be("durable-run-key");
        stateAuth.SourceCase.Should().Be(ScheduledServiceInvocationAuthState.SourceOneofCase.Durable);

        var scheduledFireAt = new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var runtimeAuth = serviceInvocationDispatch.Auths.Should().ContainSingle().Which;
        runtimeAuth.Should().NotBeNull();
        runtimeAuth!.Durable.Should().NotBeNull();
        runtimeAuth.Durable!.CredentialId.Should().Be("durable-run-key");
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be(
            "Scheduled service invocation durable credential reference exchange is not available in this phase.");
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        serviceInvocationDispatch.Auths.Should().ContainSingle();
        serviceInvocationDispatch.Requests.Should().ContainSingle();
        var idempotencyKey = ManualFireIdempotencyKey;
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be("exchange failed");
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
    }

    [Theory]
    [InlineData("WORKFLOW_DEFINITION_INVALID", "Workflow definition is invalid.")]
    [InlineData("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED", "Workflow uses a retired NyxID tool contract.")]
    [InlineData("CAPABILITY_ADMISSION_REBIND_REQUIRED", "Saved workflow and capability admission no longer match.")]
    public async Task HandleFireAsync_WhenWorkflowAdmissionIsRejected_ShouldRecordSafeTypedFailure(
        string code,
        string safeMessage)
    {
        var eventStore = new TestEventStore();
        var serviceInvocationDispatch = new RecordingScheduledServiceInvocationDispatchPort
        {
            DispatchException = CreateWorkflowAdmissionException(code, safeMessage),
        };
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            serviceInvocationDispatch: serviceInvocationDispatch);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(
            enabled: false,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity { ServiceId = "svc-alpha" },
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "configured" }),
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
                        {
                            Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                            {
                                Platform = "lark",
                                Tenant = "tenant-alpha",
                                ExternalUserId = "user-alpha",
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.LastError.Should().Be(safeMessage);
        agent.State.LastErrorCode.Should().Be(code);
        var record = agent.State.FireRecords[ManualFireIdempotencyKey];
        record.Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        record.Error.Should().Be(safeMessage);
        record.ErrorCode.Should().Be(code);
        record.TargetActorId.Should().BeEmpty();
        agent.State.Deleted.Should().BeFalse();
        agent.State.Target.ServiceInvocation.Identity.ServiceId.Should().Be("svc-alpha");
        agent.State.ToString().Should().NotContain("workflow yaml");
        agent.State.ToString().Should().NotContain(nameof(ScheduledWorkflowAdmissionException));
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var idempotencyKey = ManualFireIdempotencyKey;
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        var idempotencyKey = ManualFireIdempotencyKey;
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
            IdempotencyKey = ManualFireIdempotencyKey,
        });

        await act.Should().ThrowAsync<OperationCanceledException>();
        var idempotencyKey = ManualFireIdempotencyKey;
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
    public async Task OnActivateAsync_WhenArmedFireIsDueAndNoPendingIntent_ShouldRearmForArmedTimeAsCatchUpInsteadOfSkipping()
    {
        // Regression: a daily cron armed NextFireAt for a fire time that came due while the
        // actor was inactive (pod churn at the fire boundary). PendingNextFireAt is null in
        // steady state, so reactivation must NOT recompute the next occurrence from "now"
        // (which would skip the due fire), but re-arm for the armed time as a catch-up.
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        // Arm NextFireAt for a fire time in the past (the occurrence that came due while the
        // actor was offline) via the early-callback re-arm path. This persists a
        // NextFireScheduledEvent that sets NextFireAt and clears PendingNextFireAt, exactly
        // like steady state after a normal arm.
        var armedFireAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var firstRequest = scheduler.TimeoutRequests.Single();
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            firstRequest,
            generation: 1,
            fireIndex: 1,
            firedAt: armedFireAt.AddSeconds(-1),
            scheduledFireAt: armedFireAt));

        agent.State.NextFireAt.Should().Be(armedFireAt);
        agent.State.PendingNextFireAt.Should().BeNull();
        dispatch.Dispatches.Should().BeEmpty();
        var armCount = scheduler.TimeoutRequests.Count;

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        // The reactivation re-arms for the armed (past) time as a catch-up: a new timeout is
        // registered for armedFireAt with a near-immediate due time, and NextFireAt is NOT
        // advanced to a future occurrence (which is the silent-skip bug).
        scheduler.TimeoutRequests.Should().HaveCount(armCount + 1);
        var reactivationRequest = scheduler.TimeoutRequests[^1];
        reactivationRequest.CallbackId.Should().Be(NextFireCallbackId);
        reactivationRequest.DueTime.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
        var reactivationFire = reactivationRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        reactivationFire.Manual.Should().BeFalse();
        reactivationFire.ScheduledFireAt.ToDateTimeOffset().Should().Be(armedFireAt);
        reactivated.State.NextFireAt.Should().Be(armedFireAt);
        reactivated.State.NextFireLease.Should().NotBeNull();
    }

    [Fact]
    public async Task OnActivateAsync_WhenNothingArmedAndNoPendingIntent_ShouldComputeNextFireFromNow()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();

        // Seed an enabled, configured schedule whose persisted state has neither an armed
        // NextFireAt nor a PendingNextFireAt (genuine first activation): the scheduler throws
        // on the configure arm so only the ConfiguredEvent is persisted.
        var seed = CreateAgent(eventStore, dispatch, scheduler);
        await seed.ActivateAsync();
        scheduler.ScheduleException = new InvalidOperationException("schedule failed");
        var failedConfigure = () => seed.HandleConfigureAsync(CreateConfigureCommand(
            cronExpression: "*/15 * * * *",
            enabled: true));
        await failedConfigure.Should().ThrowAsync<InvalidOperationException>();
        seed.State.NextFireAt.Should().BeNull();
        seed.State.PendingNextFireAt.Should().NotBeNull();

        // Drop the pending intent so the reactivated state has nothing armed and nothing
        // pending, isolating the genuine-first-activation branch.
        eventStore.RemoveEvents(
            ScheduleActorId,
            ScheduledDispatchNextFireIntentRecordedEvent.Descriptor.FullName);
        scheduler.ScheduleException = null;
        var armCountBeforeReactivation = scheduler.TimeoutRequests.Count;

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        // First activation with nothing armed computes the next occurrence from now, so the
        // newly armed fire is in the future (not a catch-up of a past time).
        reactivated.State.PendingNextFireAt.Should().BeNull();
        reactivated.State.NextFireLease.Should().NotBeNull();
        reactivated.State.NextFireAt.Should().NotBeNull();
        reactivated.State.NextFireAt!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
        scheduler.TimeoutRequests.Should().HaveCount(armCountBeforeReactivation + 1);
        var reactivationRequest = scheduler.TimeoutRequests[^1];
        var reactivationFire = reactivationRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>();
        reactivationFire.ScheduledFireAt.ToDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OnActivateAsync_WhenArmedFireOverduePastGrace_ShouldRecordOverdueDetection()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var armedFireAt = await ArmOverdueFireAsync(eventStore, dispatch, scheduler, overdueBy: TimeSpan.FromMinutes(30));

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        // The armed occurrence came due while the actor was dormant and is overdue well past
        // the grace window with no terminal record, so reactivation records exactly one overdue
        // detection and remembers which occurrence it was.
        reactivated.State.OverdueFireDetectedCount.Should().Be(1);
        reactivated.State.LastOverdueFireAt.Should().Be(armedFireAt);
        reactivated.State.NextFireAt.Should().Be(armedFireAt);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(x.EventType, ScheduledDispatchFireOverdueDetectedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task OnActivateAsync_WhenArmedFireWithinGrace_ShouldNotRecordOverdueDetection()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        await ArmOverdueFireAsync(eventStore, dispatch, scheduler, overdueBy: TimeSpan.FromMinutes(1));

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        // A near-boundary catch-up (armed fire only seconds/minutes late, e.g. routine pod churn)
        // is not an overdue detection: it stays within the grace window.
        reactivated.State.OverdueFireDetectedCount.Should().Be(0);
        reactivated.State.LastOverdueFireAt.Should().BeNull();
    }

    [Fact]
    public async Task OnActivateAsync_WhenOverdueAlreadyRecordedForSameOccurrence_ShouldNotDoubleCount()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var armedFireAt = await ArmOverdueFireAsync(eventStore, dispatch, scheduler, overdueBy: TimeSpan.FromMinutes(30));

        var firstReactivation = CreateAgent(eventStore, dispatch, scheduler);
        await firstReactivation.ActivateAsync();
        firstReactivation.State.OverdueFireDetectedCount.Should().Be(1);

        var secondReactivation = CreateAgent(eventStore, dispatch, scheduler);
        await secondReactivation.ActivateAsync();

        // Repeated reactivations against the same still-overdue armed occurrence must not
        // inflate the counter: detection is once-per-occurrence via persisted LastOverdueFireAt.
        secondReactivation.State.OverdueFireDetectedCount.Should().Be(1);
        secondReactivation.State.LastOverdueFireAt.Should().Be(armedFireAt);
    }

    [Fact]
    public async Task OnActivateAsync_WhenOverdueArmedFireAlreadyHasTerminalRecord_ShouldNotRecordOverdueDetection()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        // The overdue occurrence already reached a terminal (dispatched) record via a manual fire,
        // then gets armed as NextFireAt. Reactivation must not flag it as overdue: it did fire.
        var occurrence = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(occurrence),
            Manual = true,
            IdempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", occurrence),
        });
        var firstRequest = scheduler.TimeoutRequests[0];
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            firstRequest,
            generation: 1,
            fireIndex: 1,
            firedAt: occurrence.AddSeconds(-1),
            scheduledFireAt: occurrence));
        agent.State.NextFireAt.Should().Be(occurrence);

        var reactivated = CreateAgent(eventStore, dispatch, scheduler);
        await reactivated.ActivateAsync();

        reactivated.State.OverdueFireDetectedCount.Should().Be(0);
        reactivated.State.LastOverdueFireAt.Should().BeNull();
    }

    private static async Task<DateTimeOffset> ArmOverdueFireAsync(
        TestEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        RecordingRuntimeCallbackScheduler scheduler,
        TimeSpan overdueBy)
    {
        var agent = CreateAgent(eventStore, dispatch, scheduler);
        await agent.ActivateAsync();
        await agent.HandleConfigureAsync(CreateConfigureCommand(cronExpression: "* * * * *", enabled: true));

        // Arm NextFireAt for a fire time in the past via the early-callback re-arm path, exactly
        // like steady state after a normal arm (NextFireAt set, PendingNextFireAt cleared).
        var armedFireAt = DateTimeOffset.UtcNow - overdueBy;
        var firstRequest = scheduler.TimeoutRequests.Single();
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            firstRequest,
            generation: 1,
            fireIndex: 1,
            firedAt: armedFireAt.AddSeconds(-1),
            scheduledFireAt: armedFireAt));
        agent.State.NextFireAt.Should().Be(armedFireAt);
        agent.State.PendingNextFireAt.Should().BeNull();
        agent.State.OverdueFireDetectedCount.Should().Be(0);
        return armedFireAt;
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_WithWrongSecretPurpose_ShouldRejectBegin()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var command = CreateTeamBeginCommand();
        command.CredentialEffectLocator.SecretPurpose = CredentialSecretPurposes.ScheduledNyxApiKey;

        var act = () => agent.HandleBeginTeamAutomationCredentialOperationAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_credential_effect_locator_purpose_invalid");
        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Unspecified);
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_WithActivationDecisionTeamMismatch_ShouldRejectBegin()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var command = CreateTeamBeginCommand();
        command.Owner.TeamId = "team-alpha";
        command.ActivationDecision.Owner.TeamId = "team-beta";

        var act = () => agent.HandleBeginTeamAutomationCredentialOperationAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_activation_decision_invalid");
        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Unspecified);
        eventStore.GetEvents(ScheduleActorId).Should().BeEmpty();
    }

    [Fact]
    public async Task TeamAutomationCredentialCandidate_WithWrongSecretPurpose_ShouldRejectBeforeCommit()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        agent.State.TeamCredentialEffectLocator!.SecretPurpose = CredentialSecretPurposes.ScheduledNyxApiKey;
        var credential = CreateTeamCredential("key-alpha");
        credential.SecretReference.Purpose = CredentialSecretPurposes.ScheduledNyxApiKey;

        var act = () => agent.HandleRecordTeamAutomationCredentialCandidateAsync(
            new RecordTeamAutomationCredentialCandidateCommand
            {
                Owner = CreateTeamOwner(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential,
                CredentialOwner = CreateCredentialOwner(),
                EffectAttemptId = agent.State.TeamAutomationEffectAttemptId,
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_credential_purpose_invalid");
        agent.State.CandidateTeamCredential.Should().BeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x =>
                x.EventType == TeamAutomationCredentialCandidateRecordedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_ShouldReplayExactlyAndRejectConflictingDigest()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), timeProvider: timeProvider);
        await agent.ActivateAsync();
        var command = CreateTeamBeginCommand();
        command.ObservationRequestId = "observation-request-alpha";

        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command);
        var firstEffectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        var replay = command.Clone();
        replay.ObservationRequestId = "observation-request-beta";
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(replay);

        agent.State.ScheduleId.Should().Be("schedule-1");
        agent.State.TeamAutomationEffectAttemptId.Should().Be(firstEffectAttemptId);
        agent.State.TeamAutomationEffectAttemptGeneration.Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => string.Equals(
                x.EventType,
                TeamAutomationCredentialOperationBeganEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Should().Be(1);
        var beginObservations = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(x => x.Stage == TeamAutomationOperationObservationStages.Begin)
            .ToArray();
        beginObservations.Should().HaveCount(2);
        beginObservations[0].NewOperationCommitted.Should().BeTrue();
        beginObservations[0].OwnsEffectAttempt.Should().BeTrue();
        beginObservations[1].NewOperationCommitted.Should().BeFalse();
        beginObservations[1].OwnsEffectAttempt.Should().BeFalse();
        beginObservations.Select(x => x.ObservationRequestId).Should().Equal(
            "observation-request-alpha",
            "observation-request-beta");
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.ProvisioningPending);

        var conflict = command.Clone();
        conflict.PermissionDigest = "digest-beta";
        conflict.ActivationDecision.AuthorizationFact.PermissionDigest = "digest-beta";
        conflict.ObservationRequestId = "observation-request-conflict";
        var act = () => agent.HandleBeginTeamAutomationCredentialOperationAsync(conflict);

        await act.Should().NotThrowAsync();

        var mutationConflict = command.Clone();
        mutationConflict.MutationDigest = "mutation-beta";
        mutationConflict.ObservationRequestId = "observation-request-mutation-conflict";
        var mutationAct = () => agent.HandleBeginTeamAutomationCredentialOperationAsync(mutationConflict);

        await mutationAct.Should().NotThrowAsync();
        var decisionConflict = command.Clone();
        decisionConflict.ActivationDecision.DisplayName = "Decision drift";
        decisionConflict.ObservationRequestId = "observation-request-decision-conflict";
        var decisionAct = () => agent.HandleBeginTeamAutomationCredentialOperationAsync(decisionConflict);

        await decisionAct.Should().NotThrowAsync();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType == TeamAutomationCredentialOperationBeganEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Count(x => x.OwnsEffectAttempt)
            .Should().Be(1);
        var rejections = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(x => x.ObservationStatus ==
                TeamAutomationOperationObservationStatusState.RejectedConflict)
            .ToArray();
        rejections.Should().HaveCount(3);
        rejections.Should().OnlyContain(x =>
            x.ErrorCode == "team_automation_operation_conflict" &&
            !x.OwnsEffectAttempt &&
            x.CandidateCredential == null &&
            x.PendingRevocationCredential == null &&
            x.CredentialEffectLocator == null);
        rejections.Select(x => x.ObservationRequestId).Should().Equal(
            "observation-request-conflict",
            "observation-request-mutation-conflict",
            "observation-request-decision-conflict");
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_AfterEffectLeaseExpires_ShouldOwnNewAttempt()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), timeProvider: timeProvider);
        await agent.ActivateAsync();
        var command = CreateTeamBeginCommand();

        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command);
        var firstEffectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command.Clone());
        agent.State.TeamAutomationEffectAttemptId.Should().Be(firstEffectAttemptId);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command.Clone());

        agent.State.TeamAutomationEffectAttemptId.Should().NotBe(firstEffectAttemptId);
        agent.State.TeamAutomationEffectAttemptGeneration.Should().Be(2);
        var beginObservations = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(x => x.Stage == TeamAutomationOperationObservationStages.Begin)
            .ToArray();
        beginObservations.Should().HaveCount(3);
        beginObservations.Select(x => (x.NewOperationCommitted, x.OwnsEffectAttempt)).Should().Equal(
            (true, true),
            (false, false),
            (false, true));
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_RetryPendingIdentity_ShouldClaimOnlyAfterLeaseExpires()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), timeProvider: timeProvider);
        await agent.ActivateAsync();
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var firstEffectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        var retry = new RetryTeamAutomationCredentialOperationCommand
        {
            Owner = CreateTeamOwner(),
            OperationId = "operation-alpha",
            IdempotencyKey = "idempotency-alpha",
            ObservationRequestId = "retry-before-expiry",
        };

        await agent.HandleRetryTeamAutomationCredentialOperationAsync(retry);
        agent.State.TeamAutomationEffectAttemptId.Should().Be(firstEffectAttemptId);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        retry.ObservationRequestId = "retry-after-expiry";
        await agent.HandleRetryTeamAutomationCredentialOperationAsync(retry);

        agent.State.TeamAutomationEffectAttemptId.Should().NotBe(firstEffectAttemptId);
        agent.State.TeamAutomationEffectAttemptGeneration.Should().Be(2);
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.ProvisioningPending);
        var observations = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(x => x.ObservationRequestId.StartsWith("retry-", StringComparison.Ordinal))
            .ToArray();
        observations.Select(x => x.OwnsEffectAttempt).Should().Equal(false, true);
        observations.Should().OnlyContain(x =>
            x.Stage == TeamAutomationOperationObservationStages.Begin &&
            !x.NewOperationCommitted);
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_ShouldRejectStaleEffectAttempt()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), timeProvider: timeProvider);
        await agent.ActivateAsync();
        var command = CreateTeamBeginCommand();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");

        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command);
        var staleEffectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(command.Clone());

        var recordCandidate = () => agent.HandleRecordTeamAutomationCredentialCandidateAsync(
            new RecordTeamAutomationCredentialCandidateCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                CredentialOwner = CreateCredentialOwner(),
                EffectAttemptId = staleEffectAttemptId,
            });
        var complete = () => agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = staleEffectAttemptId,
            });
        var fail = () => agent.HandleFailTeamAutomationCredentialOperationAsync(
            new FailTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                ErrorCode = "candidate_activation_failed",
                EffectAttemptId = staleEffectAttemptId,
            });

        await recordCandidate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_effect_attempt_stale");
        await complete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_effect_attempt_stale");
        await fail.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_effect_attempt_stale");
    }

    [Fact]
    public async Task TeamAutomationCredentialCandidate_ShouldSurviveReactivationAndComplete()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort(), timeProvider: timeProvider);
        await agent.ActivateAsync();
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);

        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            timeProvider: timeProvider);
        await reactivated.ActivateAsync();

        reactivated.State.CandidateTeamCredential!.ApiKeyId.Should().Be("key-alpha");
        reactivated.State.CandidateTeamCredentialOwner.Should().BeEquivalentTo(CreateCredentialOwner());
        reactivated.State.TeamAutomationEffectAttemptId.Should().Be(effectAttemptId);
        await reactivated.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = effectAttemptId,
            });

        reactivated.State.ActiveTeamCredential!.ApiKeyId.Should().Be("key-alpha");
        reactivated.State.CandidateTeamCredential.Should().BeNull();
    }

    [Fact]
    public async Task TeamAutomationCredentialFailure_WithCommittedCandidate_ShouldOwnRevocationAttempt()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var provisioningAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);

        await agent.HandleFailTeamAutomationCredentialOperationAsync(
            new FailTeamAutomationCredentialOperationCommand
            {
                Owner = owner,
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                ErrorCode = "candidate_activation_failed",
                EffectAttemptId = provisioningAttemptId,
                ObservationRequestId = "observation-fail-alpha",
            });

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.RevocationPending);
        agent.State.PendingRevocationTeamCredential!.ApiKeyId.Should().Be("key-alpha");
        agent.State.CandidateTeamCredential.Should().BeNull();
        agent.State.TeamAutomationEffectAttemptId.Should().NotBe(provisioningAttemptId);
        var failure = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.Stage == TeamAutomationOperationObservationStages.Fail);
        failure.ObservationRequestId.Should().Be("observation-fail-alpha");
        failure.OwnsEffectAttempt.Should().BeTrue();
        failure.EffectAttemptId.Should().Be(agent.State.TeamAutomationEffectAttemptId);
        failure.NyxidRevocationPending.Should().BeTrue();
        failure.VaultRevocationPending.Should().BeTrue();
    }

    [Fact]
    public async Task TeamAutomationCredentialRecoveryBlocked_ShouldNotClaimAnotherEffectAttempt()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            timeProvider: timeProvider);
        await agent.ActivateAsync();
        var begin = CreateTeamBeginCommand();
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(begin);
        var effectAttemptId = agent.State.TeamAutomationEffectAttemptId;

        await agent.HandleFailTeamAutomationCredentialOperationAsync(
            new FailTeamAutomationCredentialOperationCommand
            {
                Owner = CreateTeamOwner(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                ErrorCode = "scheduled_credential_recovery_evidence_missing",
                EffectAttemptId = effectAttemptId,
        });
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        var replay = begin.Clone();
        replay.ObservationRequestId = "recovery-blocked-replay";
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(replay);

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.Failed);
        agent.State.LastAuthorizationErrorCode.Should()
            .Be("scheduled_credential_recovery_evidence_missing");
        agent.State.TeamAutomationEffectAttemptClaimed.Should().BeFalse();
        agent.State.TeamAutomationEffectAttemptGeneration.Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Last(x => x.Stage == TeamAutomationOperationObservationStages.Begin)
            .OwnsEffectAttempt.Should().BeFalse();
    }

    [Fact]
    public async Task TeamAutomationDelete_WithCommittedCandidate_ShouldFailBeforeDroppingDescriptor()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);

        var delete = () => agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
        {
            Reason = "test",
            TeamAutomationOwner = owner,
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        });

        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_operation_in_progress");
        agent.State.CandidateTeamCredential!.ApiKeyId.Should().Be("key-alpha");
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x => x.EventType == TeamAutomationDeletionRequestedEvent.Descriptor.FullName);
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x => x.EventType == ScheduledDispatchDeletedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task TeamAutomationComplete_ShouldRejectEveryCommittedActivationDecisionSubstitution()
    {
        var substitutions = CreateTeamActivationDecisionSubstitutions();

        foreach (var (name, mutate) in substitutions)
        {
            var eventStore = new TestEventStore();
            var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
            await agent.ActivateAsync();
            var owner = CreateTeamOwner();
            var credential = CreateTeamCredential($"key-{name}");
            var configuration = CreateTeamActivationConfiguration(owner, credential);
            await agent.HandleBeginTeamAutomationCredentialOperationAsync(
                CreateTeamBeginCommand(configuration));
            var effectAttemptId = await RecordTeamCredentialCandidateAsync(
                agent,
                owner,
                "operation-alpha",
                "idempotency-alpha",
                credential);
            var substitutedConfiguration = ToConfiguredEvent(configuration);
            mutate(substitutedConfiguration);
            SynchronizePreparedServiceInvocation(substitutedConfiguration);

            await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
                new CompleteTeamAutomationCredentialOperationCommand
                {
                    Owner = owner.Clone(),
                    OperationId = "operation-alpha",
                    IdempotencyKey = "idempotency-alpha",
                    Credential = credential.Clone(),
                    Configuration = substitutedConfiguration,
                    EffectAttemptId = effectAttemptId,
                    ObservationRequestId = $"complete-{name}",
                });

            agent.State.TeamAutomationLifecycleStatus.Should()
                .Be(TeamAutomationLifecycleStatusState.ProvisioningPending, name);
            agent.State.CandidateTeamCredential.Should().BeEquivalentTo(credential, name);
            eventStore.GetEvents(ScheduleActorId).Should().NotContain(
                stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName,
                name);
            var observation = eventStore.GetEvents(ScheduleActorId)
                .Where(stored => stored.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
                .Select(stored => stored.EventData.Unpack<TeamAutomationOperationObservedEvent>())
                .Last(stored => stored.Stage == TeamAutomationOperationObservationStages.Complete);
            observation.ObservationStatus.Should()
                .Be(TeamAutomationOperationObservationStatusState.RejectedConflict, name);
            observation.ErrorCode.Should().Be("team_automation_activation_decision_mismatch", name);
        }
    }

    [Fact]
    public async Task TeamAutomationCredential_ShouldBindCandidateAndCompleteToActorOwnedReference()
    {
        var locatorSubstitutions = new (
            string Name,
            Action<SecretReference> Mutate,
            string ExpectedError)[]
        {
            (
                "reference",
                reference => reference.Ref = "secret-substituted",
                "team_automation_candidate_credential_locator_mismatch"),
            (
                "purpose",
                reference => reference.Purpose = "purpose-substituted",
                "team_automation_credential_purpose_invalid"),
            (
                "owner-scope",
                reference => reference.OwnerScopeKey = "scope-substituted",
                "team_automation_candidate_credential_locator_mismatch"),
        };
        foreach (var (name, mutate, expectedError) in locatorSubstitutions)
        {
            var agent = CreateAgent(new TestEventStore(), new RecordingActorDispatchPort());
            await agent.ActivateAsync();
            await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
            var credential = CreateTeamCredential($"candidate-{name}");
            mutate(credential.SecretReference);

            var action = () => RecordTeamCredentialCandidateAsync(
                agent,
                CreateTeamOwner(),
                "operation-alpha",
                "idempotency-alpha",
                credential);

            await action.Should().ThrowAsync<InvalidOperationException>(name)
                .WithMessage(expectedError);
            agent.State.CandidateTeamCredential.Should().BeNull(name);
        }

        var referenceSubstitutions = new (string Name, Action<SecretReference> Mutate)[]
        {
            ("purpose", reference => reference.Purpose = "purpose-substituted"),
            ("owner-scope", reference => reference.OwnerScopeKey = "scope-substituted"),
            ("fingerprint", reference => reference.Fingerprint = "fingerprint-substituted"),
            ("version", reference => reference.Version++),
            ("created-at", reference => reference.CreatedAtUnixMs++),
            ("expires-at", reference => reference.ExpiresAtUnixMs++),
        };
        foreach (var (name, mutate) in referenceSubstitutions)
        {
            var eventStore = new TestEventStore();
            var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
            await agent.ActivateAsync();
            var owner = CreateTeamOwner();
            var credential = CreateTeamCredential($"complete-{name}");
            var configuration = CreateTeamActivationConfiguration(owner, credential);
            await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
            var effectAttemptId = await RecordTeamCredentialCandidateAsync(
                agent,
                owner,
                "operation-alpha",
                "idempotency-alpha",
                credential);
            var substituted = credential.Clone();
            mutate(substituted.SecretReference);
            var completed = ToConfiguredEvent(configuration);
            completed.Target.ServiceInvocation.Auth.ScheduledInvocationAgentKey = substituted.Clone();

            await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
                new CompleteTeamAutomationCredentialOperationCommand
                {
                    Owner = owner.Clone(),
                    OperationId = "operation-alpha",
                    IdempotencyKey = "idempotency-alpha",
                    Credential = substituted,
                    Configuration = completed,
                    EffectAttemptId = effectAttemptId,
                    ObservationRequestId = $"complete-credential-{name}",
                });

            agent.State.TeamAutomationLifecycleStatus.Should()
                .Be(TeamAutomationLifecycleStatusState.ProvisioningPending, name);
            agent.State.CandidateTeamCredential.Should().BeEquivalentTo(credential, name);
            eventStore.GetEvents(ScheduleActorId).Should().NotContain(
                stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName,
                name);
        }
    }

    [Fact]
    public async Task TeamAutomationComplete_ShouldRejectNonServicePreparedPayload()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        var configuration = CreateTeamActivationConfiguration(owner, credential);
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);
        var completed = ToConfiguredEvent(configuration);
        completed.TriggerEnvelope.Payload = Any.Pack(new Empty());

        var action = () => agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = completed,
                EffectAttemptId = effectAttemptId,
            });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_configuration_not_applied");
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.ProvisioningPending);
        eventStore.GetEvents(ScheduleActorId).Should().NotContain(
            stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName);
    }

    [Theory]
    [InlineData("run-origin")]
    [InlineData("requested-run-id")]
    [InlineData("workflow-completion-target")]
    [InlineData("service-run-completion-target")]
    public async Task TeamAutomationComplete_ShouldRejectNonCanonicalPreparedServiceInvocationControlField(
        string field)
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential($"key-{field}");
        var configuration = CreateTeamActivationConfiguration(owner, credential);
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);
        var completed = ToConfiguredEvent(configuration);
        var prepared = completed.TriggerEnvelope.Payload.Unpack<ServiceInvocationRequest>();
        switch (field)
        {
            case "run-origin":
                prepared.RunOrigin = "work-order";
                break;
            case "requested-run-id":
                prepared.RequestedRunId = "run-substituted";
                break;
            case "workflow-completion-target":
                prepared.WorkflowCompletionNotificationTarget = new WorkflowServiceCompletionNotificationTarget
                {
                    ActorId = "workflow-delivery-substituted",
                    DeliveryId = "workflow-delivery-id-substituted",
                    ExpiresAtUnixMs = long.MaxValue,
                };
                break;
            case "service-run-completion-target":
                prepared.ServiceRunCompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
                {
                    ActorId = "service-run-delivery-substituted",
                    DeliveryId = "service-run-delivery-id-substituted",
                    ExpiresAtUnixMs = long.MaxValue,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
        completed.TriggerEnvelope.Payload = Any.Pack(prepared);

        var action = () => agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = completed,
                EffectAttemptId = effectAttemptId,
            });

        await action.Should().ThrowAsync<InvalidOperationException>(field)
            .WithMessage("team_automation_configuration_not_applied");
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.ProvisioningPending, field);
        eventStore.GetEvents(ScheduleActorId).Should().NotContain(
            stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName,
            field);
    }

    [Fact]
    public async Task TeamAutomationComplete_LegacyPendingWithoutDecisionShouldFailMissingBeforeLeaseOrCandidate()
    {
        var agent = CreateAgent(new TestEventStore(), new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        var configuration = CreateTeamActivationConfiguration(owner, credential);
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        agent.State.TeamAutomationActivationDecision = null;
        agent.State.TeamAutomationEffectAttemptId = string.Empty;

        var action = () => agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(configuration),
                EffectAttemptId = "legacy-effect-attempt",
            });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_activation_decision_missing");
    }

    [Fact]
    public async Task TeamAutomationComplete_ActiveReplayShouldRejectChangedConfiguration()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        var configuration = CreateTeamActivationConfiguration(owner, credential);
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);
        var complete = new CompleteTeamAutomationCredentialOperationCommand
        {
            Owner = owner.Clone(),
            OperationId = "operation-alpha",
            IdempotencyKey = "idempotency-alpha",
            Credential = credential.Clone(),
            Configuration = ToConfiguredEvent(configuration),
            EffectAttemptId = effectAttemptId,
            ObservationRequestId = "complete-initial",
        };
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(complete);

        var exactReplay = complete.Clone();
        exactReplay.ObservationRequestId = "complete-exact-replay";
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(exactReplay);
        var changedReplay = complete.Clone();
        changedReplay.Configuration.DisplayName = "Substituted after activation";
        changedReplay.ObservationRequestId = "complete-changed-replay";
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(changedReplay);

        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.DisplayName.Should().Be(configuration.DisplayName);
        eventStore.GetEvents(ScheduleActorId)
            .Count(stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName)
            .Should().Be(1);
        var completeObservations = eventStore.GetEvents(ScheduleActorId)
            .Where(stored => stored.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(stored => stored.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(stored => stored.Stage == TeamAutomationOperationObservationStages.Complete)
            .ToArray();
        completeObservations.Should().HaveCount(3);
        completeObservations[1].ObservationStatus.Should()
            .Be(TeamAutomationOperationObservationStatusState.Committed);
        completeObservations[2].ObservationStatus.Should()
            .Be(TeamAutomationOperationObservationStatusState.RejectedConflict);
        completeObservations[2].ErrorCode.Should().Be("team_automation_activation_decision_mismatch");
    }

    [Fact]
    public async Task TeamAutomationComplete_ShouldAcceptCanonicalGrantAndNodeReordering()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        var configuration = CreateTeamActivationConfiguration(owner, credential);
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);
        var completed = ToConfiguredEvent(configuration);
        var fact = completed.Target.ServiceInvocation.AuthorizationFact;
        var reorderedGrants = fact.ServiceGrants.Select(static grant => grant.Clone()).Reverse().ToArray();
        fact.ServiceGrants.Clear();
        foreach (var grant in reorderedGrants)
        {
            var reorderedNodes = grant.NodeIds.Reverse().ToArray();
            grant.NodeIds.Clear();
            grant.NodeIds.Add(reorderedNodes);
            fact.ServiceGrants.Add(grant);
        }

        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = completed,
                EffectAttemptId = effectAttemptId,
            });

        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Active);
        eventStore.GetEvents(ScheduleActorId).Should().ContainSingle(
            stored => stored.EventType == TeamAutomationCredentialActivatedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task TeamAutomationCredentialOperation_ShouldCommitConfigurationOnlyWithActivation()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", credential);
        var scheduledFireAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var beforeActivation = () => agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
            TeamAutomationOwner = owner.Clone(),
        });
        await beforeActivation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");

        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = effectAttemptId,
            });
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
            TeamAutomationOwner = owner.Clone(),
        });

        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.TeamCredentialGeneration.Should().Be(1);
        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task TeamAutomationFire_WhenCredentialCannotResolve_ShouldRequireAuthorizationWithStableCode()
    {
        var eventStore = new TestEventStore();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort
        {
            DispatchException = new ScheduledServiceInvocationAuthorizationException(
                ScheduledServiceInvocationAuthorizationFailureCode.CredentialUnresolvable,
                "vault backend detail must not become product state"),
        };
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            serviceInvocationDispatch: serviceDispatch);
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", credential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = effectAttemptId,
            });
        var scheduledFireAt = DateTimeOffset.UtcNow.AddMinutes(1);

        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
            TeamAutomationOwner = owner.Clone(),
        });

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        agent.State.LastAuthorizationErrorCode.Should().Be("credential_unresolvable");
        agent.State.LastError.Should().Be("credential_unresolvable");
        agent.State.FireCount.Should().Be(1);
        agent.State.FailureCount.Should().Be(1);
        agent.State.FireRecords[ManualFireIdempotencyKey].Status.Should()
            .Be(ScheduledDispatchFireStatusState.Failed);
        eventStore.GetEvents(ScheduleActorId)
            .Should().ContainSingle(x => x.EventType == TeamAutomationAuthorizationRequiredEvent.Descriptor.FullName);
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x => x.EventType == ScheduledDispatchFireFailedEvent.Descriptor.FullName);
        agent.State.ToString().Should().NotContain("vault backend detail");
    }

    [Fact]
    public async Task TeamAutomationAutomaticFire_WithStaleLeaseAndExpiredCredential_ShouldNotMutateState()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            serviceDispatch,
            timeProvider);
        await agent.ActivateAsync();
        var credential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(credential, timeProvider.GetUtcNow().AddMinutes(1));
        await ActivateTeamAutomationAsync(agent, credential, enabled: true);
        var fireRequest = scheduler.TimeoutRequests.Single(x => x.CallbackId == NextFireCallbackId);
        var eventCount = eventStore.GetEvents(ScheduleActorId).Count;
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            fireRequest,
            generation: agent.State.NextFireLease!.Generation + 1,
            fireIndex: 1,
            firedAt: fireRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>()
                .ScheduledFireAt.ToDateTimeOffset().AddMilliseconds(1)));

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCount);
        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.LastAuthorizationErrorCode.Should().BeEmpty();
        agent.State.FireRecords.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        serviceDispatch.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TeamAutomationAutomaticFire_WithCurrentLeaseAndExpiredCredential_ShouldRequireAuthorization()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            serviceDispatch,
            timeProvider);
        await agent.ActivateAsync();
        var credential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(credential, timeProvider.GetUtcNow().AddMinutes(1));
        await ActivateTeamAutomationAsync(agent, credential, enabled: true);
        var fireRequest = scheduler.TimeoutRequests.Single(x => x.CallbackId == NextFireCallbackId);
        var scheduledFireAt = fireRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>()
            .ScheduledFireAt.ToDateTimeOffset();
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            fireRequest,
            generation: agent.State.NextFireLease!.Generation,
            fireIndex: 1,
            firedAt: scheduledFireAt.AddMilliseconds(1)));

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        agent.State.LastAuthorizationErrorCode.Should().Be("credential_expired");
        agent.State.FireRecords.Should().BeEmpty();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Select(x => x.CallbackId).Should().Contain(
            NextFireCallbackId,
            TeamCredentialExpiryCallbackId);
        serviceDispatch.Requests.Should().BeEmpty();
        eventStore.GetEvents(ScheduleActorId)
            .Should().ContainSingle(x => x.EventType == TeamAutomationAuthorizationRequiredEvent.Descriptor.FullName);
    }

    [Theory]
    [InlineData(
        ScheduledServiceInvocationAuthorizationFailureCode.AuthorizationFactInvalid,
        "authorization_fact_invalid")]
    [InlineData(
        ScheduledServiceInvocationAuthorizationFailureCode.CallerAuthorityInvalid,
        "caller_authority_invalid")]
    [InlineData(
        ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMSelectionInvalid,
        "owner_llm_selection_invalid")]
    [InlineData(
        ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMPayloadMismatch,
        "owner_llm_payload_mismatch")]
    public async Task TeamAutomationAutomaticFire_WithAuthorizationFailure_ShouldFailOccurrenceAndRequireAuthorization(
        ScheduledServiceInvocationAuthorizationFailureCode failureCode,
        string stableCode)
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort
        {
            DispatchException = new ScheduledServiceInvocationAuthorizationException(
                failureCode,
                "expired authorization detail must not become product state"),
        };
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            serviceDispatch);
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(agent, CreateTeamCredential("key-alpha"), enabled: true);
        var fireRequest = scheduler.TimeoutRequests.Single(x => x.CallbackId == NextFireCallbackId);
        var scheduledFireAt = fireRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>()
            .ScheduledFireAt.ToDateTimeOffset();

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            fireRequest,
            generation: agent.State.NextFireLease!.Generation,
            fireIndex: 1,
            firedAt: scheduledFireAt.AddMilliseconds(1)));

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        agent.State.LastAuthorizationErrorCode.Should().Be(stableCode);
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", scheduledFireAt);
        agent.State.FireRecords[idempotencyKey].Status.Should().Be(ScheduledDispatchFireStatusState.Failed);
        agent.State.FireRecords[idempotencyKey].Error.Should().Be(stableCode);
        agent.State.ToString().Should().NotContain("expired authorization detail");
        serviceDispatch.Requests.Should().ContainSingle();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Select(x => x.CallbackId).Should().Contain(
            NextFireCallbackId,
            TeamCredentialExpiryCallbackId);
    }

    [Fact]
    public async Task TeamAutomationOneShot_WithExpiredCredential_ShouldStopUntilReauthorized()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            new RecordingScheduledServiceInvocationDispatchPort(),
            timeProvider);
        await agent.ActivateAsync();
        var credential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(credential, timeProvider.GetUtcNow().AddMinutes(1));
        await ActivateTeamAutomationAsync(
            agent,
            credential,
            enabled: true,
            oneShotFireAt: DateTimeOffset.UtcNow.AddMinutes(1));
        var fireRequest = scheduler.TimeoutRequests.Single(x => x.CallbackId == NextFireCallbackId);
        var scheduledFireAt = fireRequest.TriggerEnvelope.Payload.Unpack<ScheduledDispatchFireCommand>()
            .ScheduledFireAt.ToDateTimeOffset();
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            fireRequest,
            generation: agent.State.NextFireLease!.Generation,
            fireIndex: 1,
            firedAt: scheduledFireAt.AddMilliseconds(1)));

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        agent.State.Completed.Should().BeFalse();
        agent.State.NextFireAt.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        scheduler.TimeoutRequests.Should().HaveCount(2);
        scheduler.Canceled.Select(x => x.CallbackId).Should().Contain(
            NextFireCallbackId,
            TeamCredentialExpiryCallbackId);
    }

    [Fact]
    public async Task TeamAutomationCredentialExpiry_WhenDisabled_ShouldRequireAuthorization()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            timeProvider: timeProvider);
        await agent.ActivateAsync();
        var credential = CreateTeamCredential("key-alpha");
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(10);
        SetCredentialExpiry(credential, expiresAt);

        await ActivateTeamAutomationAsync(agent, credential, enabled: false);

        var expiryRequest = scheduler.TimeoutRequests
            .Single(x => x.CallbackId == TeamCredentialExpiryCallbackId);
        var expiryGeneration = agent.State.TeamCredentialExpiryLease!.Generation;
        agent.State.Enabled.Should().BeFalse();
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.Active);

        timeProvider.Advance(TimeSpan.FromMinutes(11));
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            expiryRequest,
            expiryGeneration,
            fireIndex: 1,
            firedAt: expiresAt));

        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        agent.State.LastAuthorizationErrorCode.Should().Be("credential_expired");
        agent.State.TeamCredentialExpiryLease.Should().BeNull();
        agent.State.NextFireLease.Should().BeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Should().ContainSingle(x =>
                x.EventType == TeamAutomationAuthorizationRequiredEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task TeamAutomationCredentialExpiry_WhenPaused_ShouldKeepExpiryLeaseAndRequireAuthorization()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            timeProvider: timeProvider);
        await agent.ActivateAsync();
        var credential = CreateTeamCredential("key-alpha");
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(10);
        SetCredentialExpiry(credential, expiresAt);
        await ActivateTeamAutomationAsync(agent, credential, enabled: true);
        var expiryRequest = scheduler.TimeoutRequests
            .Single(x => x.CallbackId == TeamCredentialExpiryCallbackId);
        var expiryGeneration = agent.State.TeamCredentialExpiryLease!.Generation;

        await agent.HandleDisableAsync(new ScheduledDispatchDisableCommand
        {
            TeamAutomationOwner = CreateTeamOwner(),
            Reason = "pause",
        });

        agent.State.TeamCredentialExpiryLease.Should().NotBeNull();
        scheduler.Canceled.Should().ContainSingle(x => x.CallbackId == NextFireCallbackId);
        timeProvider.Advance(TimeSpan.FromMinutes(11));
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            expiryRequest,
            expiryGeneration,
            fireIndex: 1,
            firedAt: expiresAt));

        agent.State.Enabled.Should().BeFalse();
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);
        scheduler.Canceled.Should().Contain(x => x.CallbackId == TeamCredentialExpiryCallbackId);
    }

    [Fact]
    public async Task TeamAutomationCredentialExpiry_AfterReplacement_ShouldIgnoreOldGenerationCallback()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            timeProvider: timeProvider);
        await agent.ActivateAsync();
        var firstCredential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(firstCredential, timeProvider.GetUtcNow().AddMinutes(10));
        await ActivateTeamAutomationAsync(agent, firstCredential, enabled: false);
        var staleRequest = scheduler.TimeoutRequests
            .Single(x => x.CallbackId == TeamCredentialExpiryCallbackId);
        var staleLeaseGeneration = agent.State.TeamCredentialExpiryLease!.Generation;

        var replacement = CreateTeamBeginCommand();
        replacement.OperationId = "operation-beta";
        replacement.IdempotencyKey = "idempotency-beta";
        replacement.OperationKind = TeamAutomationOperationKindState.Reauthorize;
        replacement.CredentialEffectLocator = CreateTeamCredentialEffectLocator("operation-beta");
        replacement.MutationDigest = "mutation-beta";
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(replacement);
        var secondCredential = CreateTeamCredential("key-beta", "operation-beta");
        SetCredentialExpiry(secondCredential, timeProvider.GetUtcNow().AddHours(1));
        var replacementAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            CreateTeamOwner(),
            "operation-beta",
            "idempotency-beta",
            secondCredential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = CreateTeamOwner(),
                OperationId = "operation-beta",
                IdempotencyKey = "idempotency-beta",
                EffectAttemptId = replacementAttemptId,
                Credential = secondCredential,
                Configuration = ToConfiguredEvent(
                    CreateTeamConfigureCommand(CreateTeamOwner(), secondCredential)),
            });
        var eventCount = eventStore.GetEvents(ScheduleActorId).Count;

        timeProvider.Advance(TimeSpan.FromMinutes(11));
        await agent.HandleEventAsync(CreateFiredCallbackEnvelope(
            staleRequest,
            staleLeaseGeneration,
            fireIndex: 1));

        eventStore.GetEvents(ScheduleActorId).Should().HaveCount(eventCount);
        agent.State.ActiveTeamCredential!.ApiKeyId.Should().Be("key-beta");
        agent.State.TeamCredentialGeneration.Should().Be(2);
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.Active);
    }

    [Fact]
    public async Task TeamAutomationCredentialExpiry_WhenInitialSchedulingFails_ShouldRecoverOnReactivation()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("callback scheduling unavailable"),
        };
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero));
        var credential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(credential, timeProvider.GetUtcNow().AddHours(1));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            timeProvider: timeProvider);
        await agent.ActivateAsync();

        var activation = () => ActivateTeamAutomationAsync(agent, credential, enabled: false);

        await activation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("callback scheduling unavailable");
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.PendingTeamCredentialExpiryAt.Should().NotBeNull();
        agent.State.TeamCredentialExpiryLease.Should().BeNull();

        scheduler.ScheduleException = null;
        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler,
            timeProvider: timeProvider);
        await reactivated.ActivateAsync();

        reactivated.State.TeamCredentialExpiryLease.Should().NotBeNull();
        reactivated.State.PendingTeamCredentialExpiryAt.Should().BeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationCredentialExpiryIntentRecordedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationCredentialExpiryScheduledEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_ShouldCommitPendingRevocationBeforeTombstone()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", credential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = effectAttemptId,
            });

        await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
        {
            Reason = "test",
            TeamAutomationOwner = owner.Clone(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        });

        agent.State.Deleted.Should().BeTrue();
        agent.State.PendingRevocationTeamCredential!.ApiKeyId.Should().Be("key-alpha");
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.RevocationPending);
        var eventTypes = eventStore.GetEvents(ScheduleActorId).Select(x => x.EventType).ToArray();
        Array.IndexOf(eventTypes, TeamAutomationDeletionRequestedEvent.Descriptor.FullName).Should()
            .BeLessThan(Array.IndexOf(eventTypes, ScheduledDispatchDeletedEvent.Descriptor.FullName));
        var deleted = eventStore.GetEvents(ScheduleActorId)
            .Single(x => x.EventType == ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .EventData.Unpack<ScheduledDispatchDeletedEvent>();
        deleted.ScheduleId.Should().Be("schedule-1");
        deleted.ScopeId.Should().Be("scope-alpha");
    }

    [Fact]
    public async Task TeamAutomationDelete_WhenTombstoneAppendFails_ShouldCommitNoDeleteFacts()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        eventStore.ThrowOnAppendEventType =
            ScheduledDispatchDeletedEvent.Descriptor.FullName;

        var delete = () => agent.HandleDeleteAsync(
            new ScheduledDispatchDeleteCommand
            {
                Reason = "scheduled_agent_key_canary_cleanup",
                TeamAutomationOwner = CreateTeamOwner(),
                OperationId = "operation-delete",
                IdempotencyKey = "idempotency-delete",
                AuthenticatedCredentialOwner = CreateCredentialOwner(),
            });

        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("append failed");
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x =>
                x.EventType ==
                    TeamAutomationDeletionRequestedEvent.Descriptor.FullName ||
                x.EventType ==
                    ScheduledDispatchDeletedEvent.Descriptor.FullName);
        agent.State.Deleted.Should().BeFalse();
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.PendingRevocationTeamCredential.Should().BeNull();
    }

    [Fact]
    public async Task TeamAutomationDelete_WhenCallbackPurgeFailsAfterCommit_ShouldRecoverOnActivationAndReplay()
    {
        var eventStore = new TestEventStore();
        var failingScheduler = new RecordingRuntimeCallbackScheduler
        {
            PurgeException = new InvalidOperationException("purge failed"),
        };
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            failingScheduler);
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: true);
        agent.State.NextFireLease.Should().NotBeNull();
        agent.State.TeamCredentialExpiryLease.Should().NotBeNull();
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };

        var initial = () => agent.HandleDeleteAsync(delete);

        await initial.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("purge failed");
        agent.State.Deleted.Should().BeTrue();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x =>
                x.EventType ==
                    TeamAutomationOperationObservedEvent.Descriptor.FullName &&
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>()
                    .ObservationRequestId == "delete-initial");
        failingScheduler.PurgedActors.Should().ContainSingle()
            .Which.Should().Be(ScheduleActorId);

        var recoveryScheduler = new RecordingRuntimeCallbackScheduler();
        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            recoveryScheduler);
        await reactivated.ActivateAsync();
        recoveryScheduler.PurgedActors.Should().ContainSingle()
            .Which.Should().Be(ScheduleActorId);

        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-replay";
        await reactivated.HandleDeleteAsync(replay);

        recoveryScheduler.PurgedActors.Should().HaveCount(2);
        var observation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId == "delete-replay");
        observation.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.Committed);
    }

    [Fact]
    public async Task TeamAutomationDelete_WhenObservationAppendFailsAfterCommit_ShouldPurgeAndReplay()
    {
        var eventStore = new TestEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            scheduler);
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: true);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        eventStore.ThrowOnAppendEventType =
            TeamAutomationOperationObservedEvent.Descriptor.FullName;

        var initial = () => agent.HandleDeleteAsync(delete);

        await initial.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("append failed");
        agent.State.Deleted.Should().BeTrue();
        scheduler.PurgedActors.Should().ContainSingle()
            .Which.Should().Be(ScheduleActorId);
        eventStore.ThrowOnAppendEventType = null;

        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-replay";
        await agent.HandleDeleteAsync(replay);

        scheduler.PurgedActors.Should().HaveCount(2);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
        var observation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId == "delete-replay");
        observation.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.Committed);
    }

    [Fact]
    public async Task TeamAutomationDelete_LegacyPartialStreamExactReplay_ShouldHealTombstone()
    {
        var eventStore = new TestEventStore();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                27,
                10,
                0,
                0,
                TimeSpan.Zero));
        var credential = CreateTeamCredential("key-alpha");
        SetCredentialExpiry(
            credential,
            timeProvider.GetUtcNow().AddMinutes(1));
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            timeProvider: timeProvider);
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            credential,
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await agent.HandleDeleteAsync(delete);
        eventStore.TruncateAfterEventType(
            ScheduleActorId,
            TeamAutomationDeletionRequestedEvent.Descriptor.FullName);
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            timeProvider: timeProvider);
        await reactivated.ActivateAsync();
        reactivated.State.Deleted.Should().BeFalse();
        reactivated.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.NeedsAuthorization);

        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-legacy-partial-replay";
        await reactivated.HandleDeleteAsync(replay);

        reactivated.State.Deleted.Should().BeTrue();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
        var observation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-legacy-partial-replay");
        observation.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.Committed);
    }

    [Fact]
    public async Task TeamAutomationDelete_CompactedDeletedStateWithoutReason_ShouldRejectReplay()
    {
        var seedStore = new TestEventStore();
        var seed = CreateAgent(seedStore, new RecordingActorDispatchPort());
        await seed.ActivateAsync();
        await ActivateTeamAutomationAsync(
            seed,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await seed.HandleDeleteAsync(delete);
        var compactedState = seed.State.Clone();
        compactedState.ClearTeamAutomationDeleteReason();
        compactedState.HasTeamAutomationDeleteReason.Should().BeFalse();
        var compactedStore = new TestEventStore();
        var compacted = CreateAgent(
            compactedStore,
            new RecordingActorDispatchPort(),
            snapshotStore: new TestSnapshotStore(compactedState, version: 0));
        await compacted.ActivateAsync();

        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-compacted-unknown-reason";
        await compacted.HandleDeleteAsync(replay);

        var rejection = compactedStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-compacted-unknown-reason");
        rejection.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.RejectedConflict);
        rejection.ErrorCode.Should().Be(
            "team_automation_operation_conflict");
        compactedStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x =>
                x.EventType ==
                    TeamAutomationDeletionRequestedEvent.Descriptor.FullName ||
                x.EventType ==
                    ScheduledDispatchDeletedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task TeamAutomationDelete_ShouldPersistNormalizedReasonAsReplayIdentity()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);

        await agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
        {
            Reason = " scheduled_agent_key_canary_cleanup ",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        });

        agent.State.TeamAutomationDeleteReason.Should().Be(
            "scheduled_agent_key_canary_cleanup");
        agent.State.HasTeamAutomationDeleteReason.Should().BeTrue();
        var requested = eventStore.GetEvents(ScheduleActorId)
            .Single(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .EventData
            .Unpack<TeamAutomationDeletionRequestedEvent>();
        requested.Reason.Should().Be("scheduled_agent_key_canary_cleanup");
        requested.HasReason.Should().BeTrue();

        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort());
        await reactivated.ActivateAsync();
        reactivated.State.TeamAutomationDeleteReason.Should().Be(
            "scheduled_agent_key_canary_cleanup");
        reactivated.State.HasTeamAutomationDeleteReason.Should().BeTrue();
    }

    [Fact]
    public async Task TeamAutomationDelete_PaddedOwnerReplayAfterCanonicalDelete_ShouldRemainExact()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-canonical-owner",
        };
        await agent.HandleDeleteAsync(delete);

        var replay = delete.Clone();
        replay.TeamAutomationOwner = new TeamMemberAutomationOwnerState
        {
            ScopeId = " scope-alpha ",
            MemberId = " member-alpha ",
            TeamId = " ",
        };
        replay.ObservationRequestId = "delete-padded-owner-replay";
        await agent.HandleDeleteAsync(replay);

        var observation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-padded-owner-replay");
        observation.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.Committed);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_MalformedOwnerReplay_ShouldRejectConflict()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-canonical-owner",
        };
        await agent.HandleDeleteAsync(delete);

        var replay = delete.Clone();
        replay.TeamAutomationOwner = new TeamMemberAutomationOwnerState
        {
            ScopeId = " ",
            MemberId = "member-alpha",
        };
        replay.ObservationRequestId = "delete-malformed-owner-replay";
        await agent.HandleDeleteAsync(replay);

        var rejection = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-malformed-owner-replay");
        rejection.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.RejectedConflict);
        rejection.ErrorCode.Should().Be(
            "team_automation_operation_conflict");
        rejection.OwnsEffectAttempt.Should().BeFalse();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_ReasonDriftWhileRevocationPending_ShouldRejectConflict()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await agent.HandleDeleteAsync(delete);

        var drift = delete.Clone();
        drift.Reason = "different_cleanup_reason";
        drift.ObservationRequestId = "delete-reason-drift-pending";
        await agent.HandleDeleteAsync(drift);

        var rejection = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-reason-drift-pending");
        rejection.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.RejectedConflict);
        rejection.ErrorCode.Should().Be(
            "team_automation_operation_conflict");
        rejection.OwnsEffectAttempt.Should().BeFalse();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_ReasonDriftAfterRevocationCompletes_ShouldRejectConflict()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await agent.HandleDeleteAsync(delete);
        await agent.HandleCompleteTeamAutomationRevocationAsync(
            new CompleteTeamAutomationRevocationCommand
            {
                Owner = CreateTeamOwner(),
                OperationId = "operation-delete",
                IdempotencyKey = "idempotency-delete",
                EffectAttemptId =
                    agent.State.TeamAutomationEffectAttemptId,
                NyxidRevoked = true,
                VaultRevoked = true,
            });

        var drift = delete.Clone();
        drift.Reason = "different_cleanup_reason";
        drift.ObservationRequestId = "delete-reason-drift-terminal";
        await agent.HandleDeleteAsync(drift);

        var rejection = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-reason-drift-terminal");
        rejection.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.RejectedConflict);
        rejection.ErrorCode.Should().Be(
            "team_automation_operation_conflict");
        rejection.OwnsEffectAttempt.Should().BeFalse();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType ==
                ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_LegacyFullReplay_ShouldRecoverReasonFromDeletedEvent()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(
            agent,
            CreateTeamCredential("key-alpha"),
            enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "scheduled_agent_key_canary_cleanup",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
            ObservationRequestId = "delete-initial",
        };
        await agent.HandleDeleteAsync(delete);
        eventStore.ClearTeamAutomationDeletionRequestedReason(
            ScheduleActorId);

        var reactivated = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort());
        await reactivated.ActivateAsync();

        reactivated.State.HasTeamAutomationDeleteReason
            .Should().BeTrue();
        reactivated.State.TeamAutomationDeleteReason.Should()
            .Be("scheduled_agent_key_canary_cleanup");
        var replay = delete.Clone();
        replay.ObservationRequestId = "delete-legacy-exact-replay";
        await reactivated.HandleDeleteAsync(replay);

        var observation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType ==
                TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x =>
                x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.ObservationRequestId ==
                "delete-legacy-exact-replay");
        observation.ObservationStatus.Should().Be(
            TeamAutomationOperationObservationStatusState.Committed);
    }

    [Fact]
    public async Task TeamAutomationDelete_ExactReplayShouldNotOwnTheEffectTwice()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var credential = CreateTeamCredential("key-alpha");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", credential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, credential)),
                EffectAttemptId = effectAttemptId,
            });
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "test",
            TeamAutomationOwner = owner.Clone(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        };

        await agent.HandleDeleteAsync(delete);
        await agent.HandleDeleteAsync(delete.Clone());

        var observations = eventStore.GetEvents(ScheduleActorId)
            .Where(x => string.Equals(
                x.EventType,
                TeamAutomationOperationObservedEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Where(x => x.Stage == TeamAutomationOperationObservationStages.Delete)
            .ToArray();
        observations.Should().HaveCount(2);
        observations[0].OwnsEffectAttempt.Should().BeTrue();
        observations[1].OwnsEffectAttempt.Should().BeFalse();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType == TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
    }

    [Fact]
    public async Task TeamAutomationDelete_AfterRevocationCompletes_ShouldReplayAsTerminalNoOp()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        await ActivateTeamAutomationAsync(agent, CreateTeamCredential("key-alpha"), enabled: false);
        var delete = new ScheduledDispatchDeleteCommand
        {
            Reason = "test",
            TeamAutomationOwner = CreateTeamOwner(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        };

        await agent.HandleDeleteAsync(delete);
        var effectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        await agent.HandleCompleteTeamAutomationRevocationAsync(
            new CompleteTeamAutomationRevocationCommand
            {
                Owner = CreateTeamOwner(),
                OperationId = "operation-delete",
                IdempotencyKey = "idempotency-delete",
                EffectAttemptId = effectAttemptId,
                NyxidRevoked = true,
                VaultRevoked = true,
            });

        var replay = () => agent.HandleDeleteAsync(delete.Clone());
        await replay.Should().NotThrowAsync();

        agent.State.Deleted.Should().BeTrue();
        agent.State.PendingRevocationTeamCredential.Should().BeNull();
        agent.State.ActiveTeamCredential.Should().BeNull();
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType == TeamAutomationDeletionRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        eventStore.GetEvents(ScheduleActorId)
            .Count(x => x.EventType == ScheduledDispatchDeletedEvent.Descriptor.FullName)
            .Should().Be(1);
        var replayObservation = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Last(x => x.Stage == TeamAutomationOperationObservationStages.Delete);
        replayObservation.OwnsEffectAttempt.Should().BeFalse();
        replayObservation.NyxidRevocationPending.Should().BeFalse();
        replayObservation.VaultRevocationPending.Should().BeFalse();
    }

    [Fact]
    public async Task TeamAutomationReauthorize_WhenReplacementPending_ShouldRejectConfigureAndDispatchActiveTargetAtomically()
    {
        var eventStore = new TestEventStore();
        var serviceDispatch = new RecordingScheduledServiceInvocationDispatchPort();
        var agent = CreateAgent(
            eventStore,
            new RecordingActorDispatchPort(),
            serviceInvocationDispatch: serviceDispatch);
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var activeCredential = CreateTeamCredential("key-active");
        var activeConfiguration = CreateTeamConfigureCommand(owner, activeCredential);
        var activeSelection = activeConfiguration.Target.ServiceInvocation.AuthorizationFact.OwnerLlmSelection;
        activeConfiguration.Target.ServiceInvocation.Payload = Any.Pack(new ChatRequestEvent
        {
            Prompt = "active prompt",
            ScopeId = "scope-alpha",
            LlmControl = new LLMControlContextPayload
            {
                ModelOverride = activeSelection.Model,
                NyxIdRoutePreference = activeSelection.RouteValue,
            },
        });
        activeConfiguration.Target.ServiceInvocation.Auth.CallerAuthority = new ScheduledCallerNyxIdAuthority
        {
            Platform = "lark",
            Tenant = "tenant-alpha",
            ExternalUserId = "sender-alpha",
            Scope = "proxy",
            BindingId = "bnd-owner-alpha",
        };
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(activeConfiguration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", activeCredential);
        var configured = ToConfiguredEvent(activeConfiguration);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = activeCredential.Clone(),
                Configuration = configured,
                EffectAttemptId = effectAttemptId,
            });
        var replacementBeginConfiguration = CreateTeamConfigureCommand(
            owner,
            CreateTeamCredential("replacement-placeholder"));
        replacementBeginConfiguration.Target.ServiceInvocation.AuthorizationFact.PermissionDigest = "digest-beta";
        replacementBeginConfiguration.Target.ServiceInvocation.AuthorizationFact.PolicyVersion = "policy-v2";
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(
            CreateTeamReauthorizeBeginCommand(replacementBeginConfiguration));
        var activeTarget = agent.State.Target!.Clone();
        var replacementSelection = CreateOwnerLLMSelection();
        replacementSelection.RouteValue = "/api/v1/proxy/s/chrono-llm-beta";
        replacementSelection.NyxIdUserServiceId = "nyx-llm-service-beta";
        replacementSelection.ServiceSlugSnapshot = "chrono-llm-beta";
        replacementSelection.Model = "gpt-5.6";
        var replacementTarget = activeTarget.Clone();
        replacementTarget.ServiceInvocation.Auth = null;
        replacementTarget.ServiceInvocation.AuthorizationFact.PermissionDigest = "digest-beta";
        replacementTarget.ServiceInvocation.AuthorizationFact.OwnerLlmSelection = replacementSelection;
        replacementTarget.ServiceInvocation.Payload = Any.Pack(new ChatRequestEvent
        {
            Prompt = "replacement prompt",
            ScopeId = "scope-alpha",
            LlmControl = new LLMControlContextPayload
            {
                ModelOverride = replacementSelection.Model,
                NyxIdRoutePreference = replacementSelection.RouteValue,
            },
        });
        var update = CreateUpdateCommand(
            displayName: "Rejected replacement update",
            target: replacementTarget,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow);
        update.TeamAutomationOwner = owner.Clone();

        var updateAction = () => agent.HandleConfigureAsync(update);

        await updateAction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_replacement_pending");
        agent.State.Target!.ToByteArray().Should().Equal(activeTarget.ToByteArray());
        await agent.HandleFireAsync(new ScheduledDispatchFireCommand
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
            Manual = true,
            IdempotencyKey = ManualFireIdempotencyKey,
            TeamAutomationOwner = owner.Clone(),
        });

        var dispatchedChat = serviceDispatch.Requests.Should().ContainSingle().Which.Payload
            .Unpack<ChatRequestEvent>();
        dispatchedChat.Prompt.Should().Be("active prompt");
        dispatchedChat.LlmControl.ModelOverride.Should().Be("gpt-5.5");
        dispatchedChat.LlmControl.NyxIdRoutePreference.Should()
            .Be("/api/v1/proxy/s/chrono-llm-public");
        var dispatchedAuth = serviceDispatch.Auths.Should().ContainSingle().Which;
        dispatchedAuth!.ScheduledInvocationAgentKey!.ApiKeyId.Should().Be("key-active");
        dispatchedAuth.CallerAuthority.Should().BeEquivalentTo(new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
        var dispatchedFact = serviceDispatch.AuthorizationFacts.Should().ContainSingle().Which;
        dispatchedFact!.PermissionDigest.Should().Be("digest-alpha");
        dispatchedFact.OwnerLLMSelection.Should().BeEquivalentTo(CreateOwnerLLMSelection());
        agent.State.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusState.ReplacementPending);
    }

    [Fact]
    public async Task TeamAutomationReauthorize_ShouldExposeReplacedCredentialAndKeepReplacementActive()
    {
        var eventStore = new TestEventStore();
        var agent = CreateAgent(eventStore, new RecordingActorDispatchPort());
        await agent.ActivateAsync();
        var owner = CreateTeamOwner();
        var activeCredential = CreateTeamCredential("key-active");
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand());
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-alpha", "idempotency-alpha", activeCredential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = activeCredential.Clone(),
                Configuration = ToConfiguredEvent(CreateTeamConfigureCommand(owner, activeCredential)),
                EffectAttemptId = effectAttemptId,
            });
        var replacement = CreateTeamCredential("key-replacement", "operation-beta");
        var replacementConfigurationCommand = CreateTeamConfigureCommand(owner, replacement);
        replacementConfigurationCommand.Target.ServiceInvocation.AuthorizationFact.PermissionDigest = "digest-beta";
        replacementConfigurationCommand.Target.ServiceInvocation.AuthorizationFact.PolicyVersion = "policy-v2";
        var replacementSelection = CreateOwnerLLMSelection();
        replacementSelection.Model = "gpt-5.5-replacement";
        replacementConfigurationCommand.Target.ServiceInvocation.AuthorizationFact.OwnerLlmSelection = replacementSelection;
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(
            CreateTeamReauthorizeBeginCommand(replacementConfigurationCommand));
        var replacementConfiguration = ToConfiguredEvent(replacementConfigurationCommand);
        var replacementEffectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent, owner, "operation-beta", "idempotency-beta", replacement);

        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner.Clone(),
                OperationId = "operation-beta",
                IdempotencyKey = "idempotency-beta",
                Credential = replacement.Clone(),
                Configuration = replacementConfiguration,
                EffectAttemptId = replacementEffectAttemptId,
            });

        var completion = eventStore.GetEvents(ScheduleActorId)
            .Where(x => x.EventType == TeamAutomationOperationObservedEvent.Descriptor.FullName)
            .Select(x => x.EventData.Unpack<TeamAutomationOperationObservedEvent>())
            .Single(x => x.Stage == TeamAutomationOperationObservationStages.Complete &&
                         x.OperationId == "operation-beta");
        completion.OwnsEffectAttempt.Should().BeTrue();
        completion.PendingRevocationCredential!.ApiKeyId.Should().Be("key-active");
        completion.NyxidRevocationPending.Should().BeTrue();
        completion.VaultRevocationPending.Should().BeTrue();
        agent.State.ActiveTeamCredential!.ApiKeyId.Should().Be("key-replacement");
        agent.State.ActiveTeamAuthorizationFact!.OwnerLlmSelection
            .Should().BeEquivalentTo(replacementSelection);
        agent.State.ActiveTeamAuthorizationFact.OwnerLlmSelection.Should().NotBeSameAs(replacementSelection);
        agent.State.PendingRevocationTeamCredential!.ApiKeyId.Should().Be("key-active");
        agent.State.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusState.Active);
        agent.State.TeamCredentialGeneration.Should().Be(2);

        var delete = () => agent.HandleDeleteAsync(new ScheduledDispatchDeleteCommand
        {
            Reason = "test",
            TeamAutomationOwner = owner.Clone(),
            OperationId = "operation-delete",
            IdempotencyKey = "idempotency-delete",
            AuthenticatedCredentialOwner = CreateCredentialOwner(),
        });
        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_revocation_in_progress");
        agent.State.PendingRevocationTeamCredential!.ApiKeyId.Should().Be("key-active");
        eventStore.GetEvents(ScheduleActorId)
            .Should().NotContain(x => x.EventType == TeamAutomationDeletionRequestedEvent.Descriptor.FullName);
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

    [Fact]
    public void ScheduledDispatchStateReplay_ShouldStripScheduleOwnedCredentialsFromLegacyConfiguredEvent()
    {
        var eventStore = new TestEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = CreateAgent(eventStore, dispatch);
        var transition = typeof(ScheduledDispatchGAgent)
            .GetMethod("TransitionState", BindingFlags.Instance | BindingFlags.NonPublic);
        transition.Should().NotBeNull();
        var invocation = new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity { ServiceId = "legacy-service" },
            EndpointId = "chat",
            Payload = Any.Pack(CreateCredentialBearingChatRequest("legacy-trigger")),
        };

        var replayed = transition!.Invoke(agent,
            [
                new ScheduledDispatchState(),
                new ScheduledDispatchConfiguredEvent
                {
                    ScheduleId = "schedule-1",
                    DisplayName = "Legacy schedule",
                    TargetActorId = ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                    TriggerEnvelope = CreateTriggerEnvelope(
                        ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                        invocation),
                    CronExpression = "*/15 * * * *",
                    Timezone = "UTC",
                    Enabled = false,
                    ConfiguredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Target = new ScheduledDispatchTargetState
                    {
                        Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                        ServiceInvocation = new ScheduledServiceInvocationTargetState
                        {
                            Identity = invocation.Identity.Clone(),
                            EndpointId = invocation.EndpointId,
                            Payload = Any.Pack(CreateCredentialBearingChatRequest("legacy-target")),
                        },
                    },
                    ScheduleKind = ScheduledDispatchScheduleKindState.Workflow,
                },
            ]) as ScheduledDispatchState;

        replayed.Should().NotBeNull();
        var replayedTriggerChat = replayed!.TriggerEnvelope!.Payload
            .Unpack<ServiceInvocationRequest>()
            .Payload
            .Unpack<ChatRequestEvent>();
        var replayedTargetChat = replayed.Target!.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();

        AssertScheduleOwnedCredentialFieldsStripped(replayedTriggerChat, "legacy-trigger");
        AssertScheduleOwnedCredentialFieldsStripped(replayedTargetChat, "legacy-target");
    }

    private static BeginTeamAutomationCredentialOperationCommand CreateTeamBeginCommand(
        ScheduledDispatchCreateCommand? activationConfiguration = null)
    {
        activationConfiguration ??= CreateTeamActivationConfiguration(
            CreateTeamOwner(),
            CreateTeamCredential("decision-placeholder"));
        return new BeginTeamAutomationCredentialOperationCommand
        {
            ScheduleId = "schedule-1",
            Owner = CreateTeamOwner(),
            OperationId = "operation-alpha",
            IdempotencyKey = "idempotency-alpha",
            PermissionDigest = "digest-alpha",
            PolicyVersion = "policy-v1",
            OperationKind = TeamAutomationOperationKindState.Create,
            CredentialEffectLocator = CreateTeamCredentialEffectLocator("operation-alpha"),
            ActivationDecision = ToActivationDecision(activationConfiguration),
            MutationDigest = "mutation-alpha",
        };
    }

    private static TeamAutomationActivationDecisionState ToActivationDecision(
        ScheduledDispatchCreateCommand configuration)
    {
        var invocation = configuration.Target.ServiceInvocation;
        var decision = new TeamAutomationActivationDecisionState
        {
            ScheduleId = configuration.ScheduleId,
            DisplayName = configuration.DisplayName,
            Owner = configuration.TeamAutomationOwner.Clone(),
            ServiceIdentity = invocation.Identity.Clone(),
            EndpointId = invocation.EndpointId,
            Payload = invocation.Payload.Clone(),
            CallerAuthority = invocation.Auth.CallerAuthority.Clone(),
            AuthorizationFact = invocation.AuthorizationFact.Clone(),
            CronExpression = configuration.CronExpression,
            Timezone = configuration.Timezone,
            Enabled = configuration.Enabled,
            ScheduleKind = configuration.ScheduleKind,
            ScheduleMode = configuration.ScheduleMode,
            OneShotFireAt = configuration.OneShotFireAt?.Clone(),
            CredentialRequirementTargetKind = configuration.Target.CredentialRequirementTargetKind,
            RevisionId = invocation.RevisionId,
            Caller = invocation.Caller?.Clone(),
        };
        decision.Headers.Add(configuration.Headers);
        return decision;
    }

    private static BeginTeamAutomationCredentialOperationCommand CreateTeamReauthorizeBeginCommand(
        ScheduledDispatchCreateCommand activationConfiguration)
    {
        var command = CreateTeamBeginCommand(activationConfiguration);
        command.OperationId = "operation-beta";
        command.IdempotencyKey = "idempotency-beta";
        command.PermissionDigest = "digest-beta";
        command.PolicyVersion = "policy-v2";
        command.OperationKind = TeamAutomationOperationKindState.Reauthorize;
        command.CredentialEffectLocator = CreateTeamCredentialEffectLocator("operation-beta");
        command.MutationDigest = "mutation-beta";
        return command;
    }

    private static ScheduledDispatchCreateCommand CreateTeamActivationConfiguration(
        TeamMemberAutomationOwnerState owner,
        ScheduledInvocationAgentKeyCredentialReferenceState credential)
    {
        var authorizationFact = new ScheduledInvocationAuthorizationFactState
        {
            PermissionDigest = "digest-alpha",
            PolicyVersion = "policy-v1",
            Owner = CreateCredentialOwner(),
            Scopes = "chat:invoke workflow:run",
            ExpiresAt = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            Disclosure = new ScheduledInvocationAuthorizationDisclosureState
            {
                DedicatedToSchedule = true,
                SecretManagedByAevatar = true,
                DeleteRevokesCredential = true,
            },
            Authority = new ScheduledInvocationAuthorizationAuthorityState
            {
                MemberStateVersion = 11,
                WorkflowStateVersion = 12,
                ConnectorStateVersion = 13,
                OwnerLlmStateVersion = 14,
                CatalogStateVersion = 15,
                CatalogObservedAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero)),
                CatalogFreshUntil = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 24, 2, 0, 0, TimeSpan.Zero)),
                CatalogContentDigest = "catalog-digest-alpha",
                CatalogContractVersion = "catalog-contract-v1",
                CatalogPolicyVersion = "catalog-policy-v1",
                CatalogEvaluatedAt = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 7, 24, 1, 5, 0, TimeSpan.Zero)),
            },
            OwnerLlmSelection = CreateOwnerLLMSelection(),
        };
        authorizationFact.ServiceGrants.Add(new ScheduledInvocationAuthorizationServiceGrantState
        {
            ServiceId = "service-alpha",
            NodeIds = { "node-b", "node-a" },
        });
        authorizationFact.ServiceGrants.Add(new ScheduledInvocationAuthorizationServiceGrantState
        {
            ServiceId = "service-beta",
            NodeIds = { "node-d", "node-c" },
            NodeGrantsNotRequired = true,
        });
        var payload = new ChatRequestEvent
        {
            Prompt = "hello scheduled workflow",
            ScopeId = "scope-alpha",
            LlmControl = new LLMControlContextPayload
            {
                ModelOverride = "gpt-5.5",
                NyxIdRoutePreference = "owner",
                MaxToolRoundsOverride = 4,
                UserMemoryPrompt = "remember alpha",
            },
        };
        var command = CreateConfigureCommand(
            targetActorId: ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            cronExpression: "5 */2 * * *",
            enabled: false,
            target: new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ActorId = ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                CredentialRequirementTargetKind =
                    ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = new ServiceIdentity
                    {
                        TenantId = "tenant-alpha",
                        AppId = "app-alpha",
                        Namespace = "default",
                        ServiceId = "service-alpha",
                    },
                    EndpointId = "chat",
                    Payload = Any.Pack(payload),
                    RevisionId = "revision-alpha",
                    Caller = new ServiceInvocationCaller
                    {
                        ServiceKey = "tenant-alpha:app-alpha:default:caller-alpha",
                        TenantId = "tenant-alpha",
                        AppId = "app-alpha",
                    },
                    Auth = new ScheduledServiceInvocationAuthState
                    {
                        ScheduledInvocationAgentKey = credential.Clone(),
                        CallerAuthority = new ScheduledCallerNyxIdAuthority
                        {
                            Platform = "nyxid",
                            Tenant = "tenant-alpha",
                            ExternalUserId = "owner-alpha",
                            Scope = "scope-alpha",
                            BindingId = "binding-alpha",
                        },
                    },
                    AuthorizationFact = authorizationFact,
                },
            },
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow);
        command.DisplayName = "Team alpha automation";
        command.Timezone = "Asia/Shanghai";
        command.TeamAutomationOwner = owner.Clone();
        command.Headers.Add("x-schedule-source", "studio");
        command.Headers.Add("x-trace-mode", "durable");
        return command;
    }

    private static IReadOnlyList<(string Name, Action<ScheduledDispatchConfiguredEvent> Mutate)>
        CreateTeamActivationDecisionSubstitutions() =>
    [
        ("schedule-id", configured => configured.ScheduleId = "schedule-substituted"),
        ("display-name", configured => configured.DisplayName = "Substituted display name"),
        ("team-owner", configured => configured.TeamAutomationOwner.MemberId = "member-substituted"),
        ("service-tenant", configured => configured.Target.ServiceInvocation.Identity.TenantId = "tenant-substituted"),
        ("service-app", configured => configured.Target.ServiceInvocation.Identity.AppId = "app-substituted"),
        ("service-namespace", configured => configured.Target.ServiceInvocation.Identity.Namespace = "substituted"),
        ("service-id", configured => configured.Target.ServiceInvocation.Identity.ServiceId = "service-substituted"),
        ("endpoint-id", configured => configured.Target.ServiceInvocation.EndpointId = "endpoint-substituted"),
        ("payload-prompt", configured => MutateTeamChatPayload(configured,
            chat => chat.Prompt = "substituted prompt")),
        ("payload-scope", configured => MutateTeamChatPayload(configured,
            chat => chat.ScopeId = "scope-substituted")),
        ("payload-llm-control", configured => MutateTeamChatPayload(configured,
            chat => chat.LlmControl.ModelOverride = "model-substituted")),
        ("caller-platform", configured =>
            configured.Target.ServiceInvocation.Auth.CallerAuthority.Platform = "platform-substituted"),
        ("caller-tenant", configured =>
            configured.Target.ServiceInvocation.Auth.CallerAuthority.Tenant = "tenant-substituted"),
        ("caller-external-user", configured =>
            configured.Target.ServiceInvocation.Auth.CallerAuthority.ExternalUserId = "user-substituted"),
        ("caller-scope", configured =>
            configured.Target.ServiceInvocation.Auth.CallerAuthority.Scope = "scope-substituted"),
        ("caller-binding", configured =>
            configured.Target.ServiceInvocation.Auth.CallerAuthority.BindingId = "binding-substituted"),
        ("permission-digest", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.PermissionDigest = "digest-substituted"),
        ("policy-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.PolicyVersion = "policy-substituted"),
        ("authorization-owner", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Owner.OwnerSubject = "owner-substituted"),
        ("authorization-owner-authority", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Owner.Authority = "authority-substituted"),
        ("authorization-owner-kind", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Owner.OwnerKind = "kind-substituted"),
        ("service-grants", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.ServiceGrants[0].ServiceId = "grant-substituted"),
        ("service-grant-node", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.ServiceGrants[0].NodeIds[0] = "node-substituted"),
        ("service-grant-mode", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.ServiceGrants[0].NodeGrantsNotRequired = true),
        ("authorization-scopes", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Scopes = "scope-substituted"),
        ("authorization-expiry", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.ExpiresAt.Seconds++),
        ("authorization-grants-mode", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.ServiceGrantsNotRequired = true),
        ("authorization-disclosure-dedicated", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Disclosure.DedicatedToSchedule = false),
        ("authorization-disclosure-custody", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Disclosure.SecretManagedByAevatar = false),
        ("authorization-disclosure-browser", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Disclosure.BrowserReceivesRawKey = true),
        ("authorization-disclosure-delete", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Disclosure.DeleteRevokesCredential = false),
        ("authorization-disclosure-pause", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Disclosure.PauseResumeRevokesCredential = true),
        ("authorization-member-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.MemberStateVersion++),
        ("authorization-workflow-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.WorkflowStateVersion++),
        ("authorization-connector-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.ConnectorStateVersion++),
        ("authorization-llm-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.OwnerLlmStateVersion++),
        ("authorization-catalog-version", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogStateVersion++),
        ("authorization-catalog-observed", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogObservedAt.Seconds++),
        ("authorization-catalog-fresh", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogFreshUntil.Seconds++),
        ("authorization-catalog-digest", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogContentDigest = "digest-substituted"),
        ("authorization-catalog-contract", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogContractVersion = "contract-substituted"),
        ("authorization-catalog-policy", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogPolicyVersion = "policy-substituted"),
        ("authorization-catalog-evaluated", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.Authority.CatalogEvaluatedAt.Seconds++),
        ("owner-llm-route-kind", configured => configured.Target.ServiceInvocation.AuthorizationFact
            .OwnerLlmSelection.RouteKind = LLMRouteKind.Unspecified),
        ("owner-llm-route", configured => configured.Target.ServiceInvocation.AuthorizationFact
            .OwnerLlmSelection.RouteValue = "route-substituted"),
        ("owner-llm-service", configured => configured.Target.ServiceInvocation.AuthorizationFact
            .OwnerLlmSelection.NyxIdUserServiceId = "service-substituted"),
        ("owner-llm-slug", configured => configured.Target.ServiceInvocation.AuthorizationFact
            .OwnerLlmSelection.ServiceSlugSnapshot = "slug-substituted"),
        ("owner-llm-model", configured =>
            configured.Target.ServiceInvocation.AuthorizationFact.OwnerLlmSelection.Model = "model-substituted"),
        ("cron", configured => configured.CronExpression = "10 */2 * * *"),
        ("timezone", configured => configured.Timezone = "UTC"),
        ("enabled", configured => configured.Enabled = true),
        ("schedule-kind", configured => configured.ScheduleKind = ScheduledDispatchScheduleKindState.Generic),
        ("headers-value", configured => configured.Headers["x-schedule-source"] = "substituted"),
        ("headers-extra", configured => configured.Headers.Add("x-extra", "not-committed")),
        ("schedule-mode", configured =>
        {
            configured.ScheduleMode = ScheduledDispatchScheduleModeState.OneShotAtUtc;
            configured.CronExpression = string.Empty;
            configured.OneShotFireAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(1));
        }),
        ("credential-target-kind", configured => configured.Target.CredentialRequirementTargetKind =
            ScheduledDispatchCredentialRequirementTargetKindState.Connector),
        ("revision-id", configured => configured.Target.ServiceInvocation.RevisionId = "revision-substituted"),
        ("service-caller", configured =>
            configured.Target.ServiceInvocation.Caller.ServiceKey = "service-caller-substituted"),
        ("service-caller-tenant", configured =>
            configured.Target.ServiceInvocation.Caller.TenantId = "tenant-substituted"),
        ("service-caller-app", configured =>
            configured.Target.ServiceInvocation.Caller.AppId = "app-substituted"),
    ];

    private static void MutateTeamChatPayload(
        ScheduledDispatchConfiguredEvent configured,
        Action<ChatRequestEvent> mutate)
    {
        var payload = configured.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>();
        mutate(payload);
        configured.Target.ServiceInvocation.Payload = Any.Pack(payload);
    }

    private static void SynchronizePreparedServiceInvocation(ScheduledDispatchConfiguredEvent configured)
    {
        var invocation = configured.Target?.ServiceInvocation;
        if (invocation == null || configured.TriggerEnvelope == null)
            return;

        configured.TriggerEnvelope.Payload = Any.Pack(new ServiceInvocationRequest
        {
            Identity = invocation.Identity?.Clone(),
            EndpointId = invocation.EndpointId,
            Payload = invocation.Payload?.Clone(),
            RevisionId = invocation.RevisionId,
            Caller = invocation.Caller?.Clone(),
            ScheduleId = configured.ScheduleId,
            CommandId = "command-alpha",
            CorrelationId = "correlation-alpha",
        });
    }

    private static ScheduledCredentialEffectLocatorState CreateTeamCredentialEffectLocator(string operationId) =>
        new()
        {
            CredentialName = $"studio-schedule-{operationId}",
            RequestedSecretReference = $"sec-{operationId}",
            SecretPurpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            SecretOwnerScopeKey = "schedule:schedule-1",
            CredentialOwner = CreateCredentialOwner(),
        };

    private static TeamMemberAutomationOwnerState CreateTeamOwner() => new()
    {
        ScopeId = "scope-alpha",
        MemberId = "member-alpha",
    };

    private static ScheduledInvocationAgentKeyCredentialReferenceState CreateTeamCredential(
        string apiKeyId,
        string operationId = "operation-alpha")
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        return new ScheduledInvocationAgentKeyCredentialReferenceState
        {
            ApiKeyId = apiKeyId,
            KeyExpiresAtUnixMs = expiresAt,
            SecretReference = new SecretReference
            {
                Ref = $"sec-{operationId}",
                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                Fingerprint = $"fingerprint-{apiKeyId}",
                Version = 7,
                OwnerScopeKey = "schedule:schedule-1",
                CreatedAtUnixMs = expiresAt - (long)TimeSpan.FromHours(1).TotalMilliseconds,
                ExpiresAtUnixMs = expiresAt,
            },
        };
    }

    private static async Task<string> RecordTeamCredentialCandidateAsync(
        ScheduledDispatchGAgent agent,
        TeamMemberAutomationOwnerState owner,
        string operationId,
        string idempotencyKey,
        ScheduledInvocationAgentKeyCredentialReferenceState credential)
    {
        var effectAttemptId = agent.State.TeamAutomationEffectAttemptId;
        effectAttemptId.Should().NotBeNullOrWhiteSpace();
        await agent.HandleRecordTeamAutomationCredentialCandidateAsync(
            new RecordTeamAutomationCredentialCandidateCommand
            {
                Owner = owner.Clone(),
                OperationId = operationId,
                IdempotencyKey = idempotencyKey,
                Credential = credential.Clone(),
                CredentialOwner = CreateCredentialOwner(),
                EffectAttemptId = effectAttemptId,
            });
        return effectAttemptId;
    }

    private static async Task ActivateTeamAutomationAsync(
        ScheduledDispatchGAgent agent,
        ScheduledInvocationAgentKeyCredentialReferenceState credential,
        bool enabled,
        DateTimeOffset? oneShotFireAt = null)
    {
        var owner = CreateTeamOwner();
        var configuration = CreateTeamConfigureCommand(owner, credential);
        configuration.Enabled = enabled;
        if (oneShotFireAt.HasValue)
        {
            configuration.ScheduleMode = ScheduledDispatchScheduleModeState.OneShotAtUtc;
            configuration.CronExpression = string.Empty;
            configuration.OneShotFireAt = Timestamp.FromDateTimeOffset(oneShotFireAt.Value);
        }
        await agent.HandleBeginTeamAutomationCredentialOperationAsync(CreateTeamBeginCommand(configuration));
        var effectAttemptId = await RecordTeamCredentialCandidateAsync(
            agent,
            owner,
            "operation-alpha",
            "idempotency-alpha",
            credential);
        await agent.HandleCompleteTeamAutomationCredentialOperationAsync(
            new CompleteTeamAutomationCredentialOperationCommand
            {
                Owner = owner,
                OperationId = "operation-alpha",
                IdempotencyKey = "idempotency-alpha",
                Credential = credential.Clone(),
                Configuration = ToConfiguredEvent(configuration),
                EffectAttemptId = effectAttemptId,
            });
    }

    private static void SetCredentialExpiry(
        ScheduledInvocationAgentKeyCredentialReferenceState credential,
        DateTimeOffset expiresAt)
    {
        credential.KeyExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();
        credential.SecretReference.ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();
    }

    private static ScheduledDispatchCreateCommand CreateTeamConfigureCommand(
        TeamMemberAutomationOwnerState owner,
        ScheduledInvocationAgentKeyCredentialReferenceState credential) =>
        CreateTeamActivationConfiguration(owner, credential);

    private static ScheduledInvocationAuthorizationOwnerState CreateCredentialOwner() => new()
    {
        Authority = "nyxid",
        OwnerKind = "personal",
        OwnerSubject = "owner-alpha",
    };

    private static ScheduledInvocationOwnerLLMSelection CreateOwnerLLMSelection() => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "nyx-llm-service-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
        Model = "gpt-5.5",
    };

    private static ScheduledDispatchConfiguredEvent ToConfiguredEvent(ScheduledDispatchCreateCommand command)
    {
        var triggerEnvelope = command.TriggerEnvelope?.Clone();
        if (command.Target?.ServiceInvocation is { } invocation && triggerEnvelope != null)
        {
            triggerEnvelope.Payload = Any.Pack(new ServiceInvocationRequest
            {
                Identity = invocation.Identity?.Clone(),
                EndpointId = invocation.EndpointId,
                Payload = invocation.Payload?.Clone(),
                RevisionId = invocation.RevisionId,
                Caller = invocation.Caller?.Clone(),
                ScheduleId = command.ScheduleId,
                CommandId = "command-alpha",
                CorrelationId = "correlation-alpha",
            });
        }
        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = command.ScheduleId,
            DisplayName = command.DisplayName,
            TargetActorId = command.TargetActorId,
            TriggerEnvelope = triggerEnvelope,
            CronExpression = command.CronExpression,
            Timezone = command.Timezone,
            Enabled = command.Enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            PayloadTypeUrl = command.PayloadTypeUrl,
            Target = command.Target?.Clone(),
            ScheduleKind = command.ScheduleKind,
            ScheduleMode = command.ScheduleMode,
            OneShotFireAt = command.OneShotFireAt?.Clone(),
            TeamAutomationOwner = command.TeamAutomationOwner?.Clone(),
        };
        configured.Headers.Add(command.Headers);
        return configured;
    }

    private static ScheduledDispatchGAgent CreateAgent(
        IEventStore eventStore,
        RecordingActorDispatchPort dispatch,
        RecordingRuntimeCallbackScheduler? callbackScheduler = null,
        RecordingScheduledServiceInvocationDispatchPort? serviceInvocationDispatch = null,
        TimeProvider? timeProvider = null,
        IEventSourcingSnapshotStore<ScheduledDispatchState>? snapshotStore = null)
    {
        var agent = new ScheduledDispatchGAgent(
            dispatch,
            serviceInvocationDispatch ?? new RecordingScheduledServiceInvocationDispatchPort(),
            new TestScheduledDispatchCredentialRequirementPolicy(),
            timeProvider)
        {
            Services = new TestServiceProvider(callbackScheduler ?? new RecordingRuntimeCallbackScheduler()),
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<ScheduledDispatchState>(
                    eventStore,
                    snapshotStore: snapshotStore),
        };
        SetAgentId(agent, ScheduleActorId);
        return agent;
    }

    private static ScheduledWorkflowAdmissionException CreateWorkflowAdmissionException(
        string code,
        string safeMessage) => new(code, safeMessage);

    private static EventEnvelope CreateFiredCallbackEnvelope(
        RuntimeCallbackTimeoutRequest request,
        long generation,
        long fireIndex,
        DateTimeOffset? firedAt = null,
        DateTimeOffset? scheduledFireAt = null)
    {
        var envelope = request.TriggerEnvelope.Clone();
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        if (scheduledFireAt.HasValue)
        {
            var fireCommand = envelope.Payload.Unpack<ScheduledDispatchFireCommand>();
            fireCommand.ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt.Value);
            envelope.Payload = Any.Pack(fireCommand);
        }

        var callback = envelope.EnsureRuntime().EnsureCallback();
        callback.CallbackId = request.CallbackId;
        callback.Generation = generation;
        callback.FireIndex = fireIndex;
        callback.FiredAtUnixTimeMs = (firedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
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
        ScheduledDispatchTargetState? target = null,
        ScheduledDispatchScheduleKindState scheduleKind = ScheduledDispatchScheduleKindState.Generic,
        ScheduledDispatchScheduleModeState scheduleMode = ScheduledDispatchScheduleModeState.RecurringCron,
        DateTimeOffset? oneShotFireAt = null)
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
            ScheduleKind = scheduleKind,
            ScheduleMode = scheduleMode,
            OneShotFireAt = oneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(oneShotFireAt.Value.ToUniversalTime())
                : null,
        };
    }

    private static ScheduledDispatchUpdateCommand CreateUpdateCommand(
        string scheduleId = "schedule-1",
        string displayName = "Test schedule",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null,
        ScheduledDispatchTargetState? target = null,
        ScheduledDispatchScheduleKindState scheduleKind = ScheduledDispatchScheduleKindState.Generic,
        ScheduledDispatchScheduleModeState scheduleMode = ScheduledDispatchScheduleModeState.RecurringCron,
        DateTimeOffset? oneShotFireAt = null)
    {
        return new ScheduledDispatchUpdateCommand
        {
            ScheduleId = scheduleId,
            DisplayName = displayName,
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
            ScheduleKind = scheduleKind,
            ScheduleMode = scheduleMode,
            OneShotFireAt = oneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(oneShotFireAt.Value.ToUniversalTime())
                : null,
        };
    }

    private static ScheduledDispatchEnsureCommand CreateEnsureCommand(
        string scheduleId = "schedule-1",
        string displayName = "Test schedule",
        string targetActorId = "target-actor-1",
        string cronExpression = "*/15 * * * *",
        bool enabled = false,
        EventEnvelope? triggerEnvelope = null,
        ScheduledDispatchTargetState? target = null,
        ScheduledDispatchScheduleKindState scheduleKind = ScheduledDispatchScheduleKindState.Generic,
        ScheduledDispatchScheduleModeState scheduleMode = ScheduledDispatchScheduleModeState.RecurringCron,
        DateTimeOffset? oneShotFireAt = null)
    {
        return new ScheduledDispatchEnsureCommand
        {
            ScheduleId = scheduleId,
            DisplayName = displayName,
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
            ScheduleKind = scheduleKind,
            ScheduleMode = scheduleMode,
            OneShotFireAt = oneShotFireAt.HasValue
                ? Timestamp.FromDateTimeOffset(oneShotFireAt.Value.ToUniversalTime())
                : null,
        };
    }

    private static string CreateFarFutureCronExpression()
    {
        var farFutureMonth = DateTimeOffset.UtcNow.AddMonths(2).Month;
        return $"0 0 1 {farFutureMonth} *";
    }

    private static ScheduledDispatchTargetState CreateTargetState(string targetActorId, EventEnvelope? triggerEnvelope) =>
        new()
        {
            Kind = ScheduledDispatchTargetKindState.Envelope,
            ActorId = targetActorId,
            Envelope = triggerEnvelope?.Clone(),
            EnvelopeAuthority = ScheduledDispatchEnvelopeAuthorityState.TrustedInternal,
        };

    private static ScheduledDispatchState CreateLegacyUnmarkedEnvelopeSnapshot(bool enabled)
    {
        var triggerEnvelope = CreateTriggerEnvelope("legacy-target-actor", new Empty());
        return new ScheduledDispatchState
        {
            ScheduleId = "schedule-1",
            DisplayName = "Legacy unmarked envelope schedule",
            TargetActorId = "legacy-target-actor",
            TriggerEnvelope = triggerEnvelope,
            CronExpression = "*/15 * * * *",
            Timezone = "UTC",
            Enabled = enabled,
            Target = new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = "legacy-target-actor",
                Envelope = triggerEnvelope.Clone(),
            },
        };
    }

    private static ChatRequestEvent CreateCredentialBearingChatRequest(string prompt) =>
        new()
        {
            Prompt = prompt,
            ConnectorHttpAuthorization = "Bearer connector-token",
            Headers =
            {
                [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer header-token",
                ["client"] = "kept",
            },
            Metadata =
            {
                [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer metadata-token",
                ["trace"] = "kept",
            },
            ToolContext = new AgentToolExecutionContextPayload
            {
                Credentials = new AgentToolCredentialsPayload
                {
                    NyxIdAccessToken = "tool-owner-token",
                    NyxIdOrgToken = "tool-org-token",
                    SenderNyxIdAccessToken = "tool-sender-token",
                },
            },
            LlmControl = new LLMControlContextPayload
            {
                NyxIdAccessToken = "owner-token",
                NyxIdOrgToken = "org-token",
                SenderNyxIdAccessToken = "sender-token",
                ModelOverride = "sonnet",
            },
        };

    private static void AssertScheduleOwnedCredentialFieldsStripped(ChatRequestEvent chatRequest, string prompt)
    {
        chatRequest.Prompt.Should().Be(prompt);
        chatRequest.ConnectorHttpAuthorization.Should().BeEmpty();
        chatRequest.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        chatRequest.Headers.Should().Contain("client", "kept");
        chatRequest.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        chatRequest.Metadata.Should().Contain("trace", "kept");
        chatRequest.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        chatRequest.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        chatRequest.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        chatRequest.LlmControl.ModelOverride.Should().Be("sonnet");
        chatRequest.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        chatRequest.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        chatRequest.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
    }

    private static ScheduledDispatchTargetState CreateWorkflowServiceInvocationTarget(
        ScheduledServiceInvocationAuthState? auth = null,
        ChatRequestEvent? payload = null) =>
        new()
        {
            Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService,
            ServiceInvocation = new ScheduledServiceInvocationTargetState
            {
                Identity = new ServiceIdentity { ServiceId = "configured-service" },
                EndpointId = "chat",
                Payload = Any.Pack(payload ?? new ChatRequestEvent { Prompt = "configured" }),
                Auth = auth,
            },
        };

    private static ScheduledDispatchCreateCommand CreateConditionalServiceTargetConfiguration(bool enabled)
    {
        var target = CreateWorkflowServiceInvocationTarget(CreateSenderNyxIdAuth());
        target.ServiceInvocation!.Identity = new ServiceIdentity
        {
            TenantId = "tenant-alpha",
            AppId = "app-alpha",
            Namespace = "default",
            ServiceId = "service-alpha",
        };
        var invocation = target.ServiceInvocation;
        return CreateConfigureCommand(
            targetActorId: ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            cronExpression: "*/15 * * * *",
            enabled: enabled,
            triggerEnvelope: CreateTriggerEnvelope(
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
                new ServiceInvocationRequest
                {
                    Identity = invocation.Identity.Clone(),
                    EndpointId = invocation.EndpointId,
                    Payload = invocation.Payload.Clone(),
                }),
            target: target,
            scheduleKind: ScheduledDispatchScheduleKindState.Workflow);
    }

    private static ScheduledDispatchExpectedServiceTargetState CreateExpectedServiceTarget(
        string serviceId) => new()
    {
        ScheduleKind = ScheduledDispatchScheduleKindState.Workflow,
        TargetKind = ScheduledDispatchTargetKindState.ServiceInvocation,
        ServiceIdentity = new ServiceIdentity
        {
            TenantId = "tenant-alpha",
            AppId = "app-alpha",
            Namespace = "default",
            ServiceId = serviceId,
        },
        ServiceEndpointId = "chat",
    };

    private static ScheduledServiceInvocationAuthState CreateSenderNyxIdAuth() =>
        new()
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
        };

    private static ScheduledServiceInvocationAuthState CreateLegacyDurableBearerAuth() =>
        new()
        {
            DurableSenderBearerToken = "legacy-bearer-token",
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
        public List<ScheduledDispatchFireContext?> FireContexts { get; } = [];
        public List<bool> ProjectNyxIdAccessTokenToWorkflowCallerCredentials { get; } = [];
        public List<ScheduledInvocationAuthorizationFact?> AuthorizationFacts { get; } = [];

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
            ProjectNyxIdAccessTokenToWorkflowCallerCredentials.Add(
                dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential);
            AuthorizationFacts.Add(dispatch.AuthorizationFact);
            FireContexts.Add(dispatch.FireContext);
            Headers.Add(dispatch.Headers == null
                ? null
                : new Dictionary<string, string>(dispatch.Headers, StringComparer.Ordinal));
            if (DispatchException != null)
                throw DispatchException;

            return Task.FromResult(ReceiptFactory(dispatch));
        }
    }

    private sealed class TestScheduledDispatchCredentialRequirementPolicy : IScheduledDispatchCredentialRequirementPolicy
    {
        public ScheduledDispatchCredentialRequirementDecision Evaluate(
            ScheduledDispatchCredentialRequirementRequest request)
        {
            var credentialRequired = request.TargetKind is
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService or
                ScheduledDispatchCredentialRequirementTargetKind.Connector;
            if (request.PayloadCredentialSignal.HasCurrentSessionCredential)
            {
                return ScheduledDispatchCredentialRequirementDecision.Deny(
                    credentialRequired,
                    ScheduledDispatchCredentialViolationCode.CurrentSessionCredential,
                    "Scheduled dispatch cannot persist current-session credentials.");
            }

            if (request.CredentialSource.Kind is ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer
                or ScheduledDispatchCredentialSourceKind.Multiple)
            {
                return ScheduledDispatchCredentialRequirementDecision.Deny(
                    credentialRequired,
                    ScheduledDispatchCredentialViolationCode.UnsupportedCredentialSource,
                    "Scheduled dispatch credential source is not supported.");
            }

            if (credentialRequired &&
                request.CredentialSource.Kind == ScheduledDispatchCredentialSourceKind.None)
            {
                return ScheduledDispatchCredentialRequirementDecision.Deny(
                    credentialRequired,
                    ScheduledDispatchCredentialViolationCode.CredentialRequired,
                    "Scheduled dispatch target requires a typed service invocation credential source.");
            }

            return ScheduledDispatchCredentialRequirementDecision.Allow(credentialRequired);
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly Dictionary<(string ActorId, string CallbackId), long> _generations = [];

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public List<string> PurgedActors { get; } = [];

        public Exception? ScheduleException { get; set; }

        public Exception? PurgeException { get; set; }

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
            PurgedActors.Add(actorId);
            if (PurgeException != null)
                throw PurgeException;
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

    private sealed class TestSnapshotStore(
        ScheduledDispatchState state,
        long version) : IEventSourcingSnapshotStore<ScheduledDispatchState>
    {
        private EventSourcingSnapshot<ScheduledDispatchState> _snapshot =
            new(state.Clone(), version);

        public Task<EventSourcingSnapshot<ScheduledDispatchState>?> LoadAsync(
            string agentId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<EventSourcingSnapshot<ScheduledDispatchState>?>(
                new(_snapshot.State.Clone(), _snapshot.Version));
        }

        public Task SaveAsync(
            string agentId,
            EventSourcingSnapshot<ScheduledDispatchState> snapshot,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _snapshot = new EventSourcingSnapshot<ScheduledDispatchState>(
                snapshot.State.Clone(),
                snapshot.Version);
            return Task.CompletedTask;
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

        public void RemoveEvents(string agentId, string eventType)
        {
            if (!_streams.TryGetValue(agentId, out var stream))
                return;

            stream.RemoveAll(x => string.Equals(x.EventType, eventType, StringComparison.Ordinal));
        }

        public void TruncateAfterEventType(
            string agentId,
            string eventType)
        {
            if (!_streams.TryGetValue(agentId, out var stream))
                return;

            var eventIndex = stream.FindIndex(x =>
                string.Equals(
                    x.EventType,
                    eventType,
                    StringComparison.Ordinal));
            eventIndex.Should().BeGreaterThanOrEqualTo(0);
            stream.RemoveRange(
                eventIndex + 1,
                stream.Count - eventIndex - 1);
        }

        public void ClearTeamAutomationDeletionRequestedReason(
            string agentId)
        {
            var stateEvent = _streams[agentId].Single(x =>
                x.EventType ==
                TeamAutomationDeletionRequestedEvent.Descriptor.FullName);
            var requested =
                stateEvent.EventData
                    .Unpack<TeamAutomationDeletionRequestedEvent>();
            requested.ClearReason();
            stateEvent.EventData = Any.Pack(requested);
        }

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
