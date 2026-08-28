using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorRuntimeCallbackSchedulerTests
{
    [Fact]
    public async Task DurableScheduleTimeoutAsync_ShouldAlwaysUseDedicatedSchedulerGrain()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 77 };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(77);
        lease.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);
        dedicatedGrain.ScheduleTimeoutCalls.Should().Be(1);
        dedicatedGrain.ScheduleTimerCalls.Should().Be(0);
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(1);
    }

    [Fact]
    public async Task DurableScheduleTimeoutAsync_ShouldPreserveOriginalTriggerEnvelopeSemantics()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 9 };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "parent-run",
            CallbackId = "retry-cb",
            DueTime = TimeSpan.FromMilliseconds(50),
            DeliveryMode = RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            TriggerEnvelope = new EventEnvelope
            {
                Id = "retry-envelope-1",
                Payload = Any.Pack(new StringValue { Value = "retry-payload" }),
                Route = EnvelopeRouteSemantics.CreateDirect("child-run", "parent-run"),
            },
        });

        var scheduled = dedicatedGrain.LastTimeoutTriggerEnvelope;
        dedicatedGrain.LastDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.EnvelopeRedelivery);
        scheduled.Id.Should().Be("retry-envelope-1");
        scheduled.Route!.PublisherActorId.Should().Be("child-run");
        scheduled.Route.IsDirect().Should().BeTrue();
        scheduled.Route.GetTargetActorId().Should().Be("parent-run");
    }

    [Fact]
    public async Task DurableScheduleTimerAsync_ShouldAlwaysUseDedicatedReminderScheduler()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 13 };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        var lease = await scheduler.ScheduleTimerAsync(new RuntimeCallbackTimerRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(5),
            Period = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);
        dedicatedGrain.ScheduleTimerCalls.Should().Be(1);
        dedicatedGrain.LastTimerPeriodMs.Should().Be(2000);
    }

    [Fact]
    public async Task DurableCancelAsync_ShouldUseDedicatedLeaseBackend()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain();
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        await scheduler.CancelAsync(new RuntimeCallbackLease("actor-1", "cb-1", 5, RuntimeCallbackBackend.Dedicated));

        dedicatedGrain.CancelCalls.Should().Be(1);
        dedicatedGrain.LastCancelExpectedGeneration.Should().Be(5);
        dedicatedGrain.LastCancelExpectedSlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.Unspecified);
    }

    [Fact]
    public async Task DurableScheduleAndCancelAsync_ShouldFenceGenerationWithSlotEpoch()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 1 };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        var newLease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        newLease.Generation.Should().Be(1);
        newLease.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        await scheduler.CancelAsync(new RuntimeCallbackLease("actor-1", "cb-1", 1, RuntimeCallbackBackend.Dedicated));
        dedicatedGrain.LastCancelExpectedGeneration.Should().Be(1);
        dedicatedGrain.LastCancelExpectedSlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.Unspecified);

        await scheduler.CancelAsync(newLease);
        dedicatedGrain.LastCancelExpectedGeneration.Should().Be(1);
        dedicatedGrain.LastCancelExpectedSlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
    }

    [Fact]
    public async Task PurgeActorAsync_ShouldUseDedicatedSchedulerGrain()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain();
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);

        await scheduler.PurgeActorAsync("actor-1");

        dedicatedGrain.PurgeCalls.Should().Be(1);
    }

    [Fact]
    public async Task GenericMutationApis_ShouldRejectReservedFleetReconcileSlot()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain();
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);
        var timerRequest = new RuntimeCallbackTimerRequest
        {
            ActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            CallbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            DueTime = TimeSpan.FromSeconds(1),
            Period = TimeSpan.FromSeconds(1),
            TriggerEnvelope = CreateEnvelope(),
        };

        await FluentActions.Awaiting(() => scheduler.ScheduleTimerAsync(timerRequest))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime-reserved*");
        await FluentActions.Awaiting(() => scheduler.CancelAsync(
                new RuntimeCallbackLease(
                    timerRequest.ActorId,
                    timerRequest.CallbackId,
                    1,
                    RuntimeCallbackBackend.Dedicated)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime-reserved*");
        await FluentActions.Awaiting(() => scheduler.PurgeActorAsync(timerRequest.ActorId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime-reserved*");
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(0);
    }

    [Fact]
    public async Task ProtectedFleetReconcileDelivery_ShouldRequireSchedulerGrainVerification()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain
        {
            FleetReconcileDeliveryVerified = true,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory);
        var delivered = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            generation: 9,
            fireIndex: 4,
            triggerEnvelope: new EventEnvelope
            {
                Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                    RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                    TopologyAudience.Self),
            },
            RuntimeCallbackDeliveryMode.FiredSelfEvent,
            RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        var attestation = await scheduler.VerifyAsync(delivered);

        attestation.Should().NotBeNull();
        attestation!.EnvelopeId.Should().Be(delivered.Id);
        attestation.Generation.Should().Be(9);
        attestation.FireIndex.Should().Be(4);
        dedicatedGrain.VerifyFleetReconcileDeliveryCalls.Should().Be(1);
        dedicatedGrain.LastVerifiedFleetReconcileEnvelope.Should().BeEquivalentTo(delivered);

        await scheduler.AcknowledgeDeliveryAsync(attestation);

        dedicatedGrain.AcknowledgeFleetReconcileDeliveryCalls.Should().Be(1);
        dedicatedGrain.LastAcknowledgedFleetReconcileDelivery.Should().Be(attestation);

        dedicatedGrain.FleetReconcileDeliveryVerified = false;
        (await scheduler.VerifyAsync(delivered)).Should().BeNull();
    }

    [Fact]
    public void CreateFiredEnvelope_ShouldPublishSelfContinuationWithoutOverwritingPublisher()
    {
        var fired = RuntimeCallbackEnvelopeFactory.CreateFiredEnvelope(
            actorId: "parent-run",
            callbackId: "retry-fired",
            generation: 2,
            fireIndex: 1,
            triggerEnvelope: new EventEnvelope
            {
                Payload = Any.Pack(new StringValue { Value = "retry-payload" }),
                Route = EnvelopeRouteSemantics.CreateDirect("child-run", "stale-target"),
            });

        fired.Route!.PublisherActorId.Should().Be("child-run");
        fired.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        fired.Route.IsTopologyPublication().Should().BeTrue();
        fired.Route.GetTargetActorId().Should().BeEmpty();
    }

    [Fact]
    public void CreateScheduledEnvelope_WhenEnvelopeRedelivery_ShouldPreserveOriginalDirectEnvelopeIdentity()
    {
        var original = new EventEnvelope
        {
            Id = "retry-envelope-2",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StringValue { Value = "retry-payload" }),
            Route = EnvelopeRouteSemantics.CreateDirect("child-run", "parent-run"),
        };

        var scheduled = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            actorId: "parent-run",
            callbackId: "retry-cb",
            generation: 3,
            fireIndex: 1,
            triggerEnvelope: original,
            deliveryMode: RuntimeCallbackDeliveryMode.EnvelopeRedelivery);

        scheduled.Id.Should().Be("retry-envelope-2");
        scheduled.Timestamp.Should().BeEquivalentTo(original.Timestamp);
        scheduled.Route.IsDirect().Should().BeTrue();
        scheduled.Route.PublisherActorId.Should().Be("child-run");
        scheduled.Route.GetTargetActorId().Should().Be("parent-run");
        scheduled.Runtime.Should().BeNull();
    }

    private static EventEnvelope CreateEnvelope() => new()
    {
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Route = EnvelopeRouteSemantics.CreateDirect("actor-1", "actor-1"),
    };

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<string, IRuntimeCallbackSchedulerGrain>? ResolveCallbackSchedulerGrain { get; set; }

        public int CallbackSchedulerGrainCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeCallbackSchedulerGrain) &&
                args is { Length: > 0 } &&
                args[0] is string actorId &&
                ResolveCallbackSchedulerGrain != null)
            {
                CallbackSchedulerGrainCalls++;
                return ResolveCallbackSchedulerGrain(actorId);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }

    private sealed class RecordingCallbackSchedulerGrain : IRuntimeCallbackSchedulerGrain
    {
        public long NextGeneration { get; set; } = 1;

        public bool FleetReconcileDeliveryVerified { get; set; }

        public int VerifyFleetReconcileDeliveryCalls { get; private set; }

        public EventEnvelope? LastVerifiedFleetReconcileEnvelope { get; private set; }

        public int AcknowledgeFleetReconcileDeliveryCalls { get; private set; }

        public RuntimeFleetReconcileDeliveryAttestation? LastAcknowledgedFleetReconcileDelivery { get; private set; }

        public int ScheduleTimeoutCalls { get; private set; }

        public int ScheduleTimerCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int PurgeCalls { get; private set; }

        public int LastTimerPeriodMs { get; private set; }

        public long LastCancelExpectedGeneration { get; private set; }

        public int LastCancelExpectedSlotEpoch { get; private set; }

        public EventEnvelope LastTimeoutTriggerEnvelope { get; private set; } = new();

        public RuntimeCallbackDeliveryMode LastDeliveryMode { get; private set; } = RuntimeCallbackDeliveryMode.FiredSelfEvent;

        public Task<long> ScheduleTimeoutAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            int dueTimeMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = dueTimeMs;
            LastDeliveryMode = deliveryMode;
            LastTimeoutTriggerEnvelope = triggerEnvelope.Clone();
            ScheduleTimeoutCalls++;
            return Task.FromResult(NextGeneration);
        }

        public Task<long> ScheduleTimerAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            int dueTimeMs,
            int periodMs,
            RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
        {
            _ = callbackId;
            _ = triggerEnvelope;
            _ = dueTimeMs;
            LastTimerPeriodMs = periodMs;
            LastDeliveryMode = deliveryMode;
            ScheduleTimerCalls++;
            return Task.FromResult(NextGeneration);
        }

        public Task CancelAsync(
            string callbackId,
            long expectedGeneration = 0,
            int expectedSlotEpoch = RuntimeCallbackSlotEpoch.Unspecified)
        {
            _ = callbackId;
            LastCancelExpectedGeneration = expectedGeneration;
            LastCancelExpectedSlotEpoch = expectedSlotEpoch;
            CancelCalls++;
            return Task.CompletedTask;
        }

        public Task PurgeAsync()
        {
            PurgeCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> VerifyRuntimeFleetReconcileDeliveryAsync(EventEnvelope envelope)
        {
            VerifyFleetReconcileDeliveryCalls++;
            LastVerifiedFleetReconcileEnvelope = envelope.Clone();
            return Task.FromResult(FleetReconcileDeliveryVerified);
        }

        public Task AcknowledgeRuntimeFleetReconcileDeliveryAsync(
            string envelopeId,
            long generation,
            long fireIndex,
            int slotEpoch)
        {
            AcknowledgeFleetReconcileDeliveryCalls++;
            LastAcknowledgedFleetReconcileDelivery = new RuntimeFleetReconcileDeliveryAttestation(
                envelopeId,
                generation,
                fireIndex,
                slotEpoch);
            return Task.CompletedTask;
        }
    }
}
