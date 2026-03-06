using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
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
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Timer);
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(1);
    }

    [Fact]
    public async Task DurableScheduleTimeoutAsync_ShouldSelectTimer_WhenDeliveryModeIsForcedTimer()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 13 };
        var options = new AevatarOrleansRuntimeOptions
        {
            RuntimeCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeTimer,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMinutes(10),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Timer);
    }

    [Fact]
    public async Task DurableScheduleTimeoutAsync_ShouldSelectReminder_WhenDeliveryModeIsForcedReminder()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 17 };
        var options = new AevatarOrleansRuntimeOptions
        {
            RuntimeCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeReminder,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMilliseconds(100),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Reminder);
    }

    [Fact]
    public async Task DurableScheduleTimeoutAsync_ShouldSelectReminderInAutoMode_WhenDueTimeExceedsThreshold()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 9 };
        var options = new AevatarOrleansRuntimeOptions
        {
            RuntimeCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeAuto,
            RuntimeCallbackReminderThresholdMs = 1000,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeDurableCallbackScheduler(grainFactory, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(5),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Backend.Should().Be(RuntimeCallbackBackend.Dedicated);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Reminder);
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
    }

    private static EventEnvelope CreateEnvelope() => new()
    {
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Direction = EventDirection.Self,
        TargetActorId = "actor-1",
        PublisherId = "actor-1",
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

        public int ScheduleTimeoutCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public long LastCancelExpectedGeneration { get; private set; }

        public RuntimeCallbackDeliveryMode LastTimeoutDeliveryMode { get; private set; }

        public Task<long> ScheduleTimeoutAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs,
            RuntimeCallbackDeliveryMode deliveryMode)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            LastTimeoutDeliveryMode = deliveryMode;
            ScheduleTimeoutCalls++;
            return Task.FromResult(NextGeneration);
        }

        public Task<long> ScheduleTimerAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs,
            int periodMs,
            RuntimeCallbackDeliveryMode deliveryMode)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            _ = periodMs;
            _ = deliveryMode;
            return Task.FromResult(NextGeneration);
        }

        public Task CancelAsync(string callbackId, long expectedGeneration = 0)
        {
            _ = callbackId;
            LastCancelExpectedGeneration = expectedGeneration;
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}
