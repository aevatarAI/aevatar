using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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

        public int ScheduleTimerCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int LastTimerPeriodMs { get; private set; }

        public long LastCancelExpectedGeneration { get; private set; }

        public Task<long> ScheduleTimeoutAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            ScheduleTimeoutCalls++;
            return Task.FromResult(NextGeneration);
        }

        public Task<long> ScheduleTimerAsync(
            string callbackId,
            byte[] envelopeBytes,
            int dueTimeMs,
            int periodMs)
        {
            _ = callbackId;
            _ = envelopeBytes;
            _ = dueTimeMs;
            LastTimerPeriodMs = periodMs;
            ScheduleTimerCalls++;
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
