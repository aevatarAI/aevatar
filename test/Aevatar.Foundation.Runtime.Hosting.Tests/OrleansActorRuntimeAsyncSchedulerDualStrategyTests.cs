using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Async;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorRuntimeAsyncSchedulerDualStrategyTests
{
    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldUseInlineStrategy_WhenActorTurnBindingMatches()
    {
        var inlineScheduler = new RecordingInlineScheduler("actor-1");
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => throw new InvalidOperationException("Dedicated path should not be called.");

        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(1);
        inlineScheduler.TimeoutCalls.Should().Be(1);
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldFallbackToDedicatedStrategy_WhenBindingActorMismatch()
    {
        var inlineScheduler = new RecordingInlineScheduler("another-actor");
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 77 };
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;

        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(77);
        inlineScheduler.TimeoutCalls.Should().Be(0);
        dedicatedGrain.ScheduleTimeoutCalls.Should().Be(1);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Timer);
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldThrow_WhenForceInlineButNoBinding()
    {
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceInline,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, inlineSchedulerBindingAccessor: null, options);

        var action = () => scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(1),
            TriggerEnvelope = CreateEnvelope(),
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ForceInline*");
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldUseDedicatedStrategy_WhenForceDedicatedEvenWithMatchingBinding()
    {
        var inlineScheduler = new RecordingInlineScheduler("actor-1");
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 33 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated,
        };
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(1),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(33);
        inlineScheduler.TimeoutCalls.Should().Be(0);
        dedicatedGrain.ScheduleTimeoutCalls.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldFallbackToDedicatedStrategy_WhenAutoInlineThresholdExceeded()
    {
        var inlineScheduler = new RecordingInlineScheduler("actor-1");
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 21 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackInlineMaxDueTimeMs = 500,
        };
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(2),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(21);
        inlineScheduler.TimeoutCalls.Should().Be(0);
        dedicatedGrain.ScheduleTimeoutCalls.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldSelectTimer_WhenDedicatedDeliveryModeIsForcedTimer()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 13 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated,
            AsyncCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.AsyncCallbackDedicatedDeliveryModeTimer,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, inlineSchedulerBindingAccessor: null, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMinutes(10),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(13);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Timer);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldSelectReminder_WhenDedicatedDeliveryModeIsForcedReminder()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 17 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated,
            AsyncCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.AsyncCallbackDedicatedDeliveryModeReminder,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, inlineSchedulerBindingAccessor: null, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromMilliseconds(100),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(17);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Reminder);
    }

    [Fact]
    public async Task ScheduleTimeoutAsync_ShouldSelectReminderInAutoMode_WhenDueTimeExceedsThreshold()
    {
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 9 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated,
            AsyncCallbackDedicatedDeliveryMode = AevatarOrleansRuntimeOptions.AsyncCallbackDedicatedDeliveryModeAuto,
            AsyncCallbackReminderThresholdMs = 1000,
        };
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, inlineSchedulerBindingAccessor: null, options);

        var lease = await scheduler.ScheduleTimeoutAsync(new RuntimeTimeoutRequest
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            DueTime = TimeSpan.FromSeconds(5),
            TriggerEnvelope = CreateEnvelope(),
        });

        lease.Generation.Should().Be(9);
        dedicatedGrain.LastTimeoutDeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.Reminder);
    }

    [Fact]
    public async Task CancelAsync_ShouldUseInlineStrategy_WhenActorTurnBindingMatches()
    {
        var inlineScheduler = new RecordingInlineScheduler("actor-1");
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => throw new InvalidOperationException("Dedicated path should not be called.");

        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor);

        await scheduler.CancelAsync("actor-1", "cb-1", expectedGeneration: 5);

        inlineScheduler.CancelCalls.Should().Be(1);
        inlineScheduler.LastCancelExpectedGeneration.Should().Be(5);
        grainFactoryProxy.CallbackSchedulerGrainCalls.Should().Be(0);
    }

    [Fact]
    public async Task CancelAsync_ShouldUseDedicatedStrategy_WhenForceDedicatedEvenWithMatchingBinding()
    {
        var inlineScheduler = new RecordingInlineScheduler("actor-1");
        var dedicatedGrain = new RecordingCallbackSchedulerGrain { NextGeneration = 1 };
        var options = new AevatarOrleansRuntimeOptions
        {
            AsyncCallbackSchedulingMode = AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated,
        };
        var bindingAccessor = new InlineBindingAccessor(inlineScheduler);
        var grainFactory = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        var grainFactoryProxy = (GrainFactoryProxy)(object)grainFactory;
        grainFactoryProxy.ResolveCallbackSchedulerGrain = _ => dedicatedGrain;
        var scheduler = new OrleansActorRuntimeAsyncScheduler(grainFactory, bindingAccessor, options);

        await scheduler.CancelAsync("actor-1", "cb-1", expectedGeneration: 5);

        inlineScheduler.CancelCalls.Should().Be(0);
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

    private sealed class InlineBindingAccessor : IRuntimeActorInlineCallbackSchedulerBindingAccessor
    {
        public InlineBindingAccessor(IRuntimeActorInlineCallbackScheduler current)
        {
            Current = current;
        }

        public IRuntimeActorInlineCallbackScheduler? Current { get; private set; }

        public IDisposable Bind(IRuntimeActorInlineCallbackScheduler scheduler)
        {
            var previous = Current;
            Current = scheduler;
            return new RestoreScope(this, previous);
        }

        private sealed class RestoreScope : IDisposable
        {
            private readonly InlineBindingAccessor _owner;
            private readonly IRuntimeActorInlineCallbackScheduler? _previous;
            private bool _disposed;

            public RestoreScope(
                InlineBindingAccessor owner,
                IRuntimeActorInlineCallbackScheduler? previous)
            {
                _owner = owner;
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _owner.Current = _previous;
                _disposed = true;
            }
        }
    }

    private sealed class RecordingInlineScheduler : IRuntimeActorInlineCallbackScheduler
    {
        private long _generation;

        public RecordingInlineScheduler(string actorId)
        {
            ActorId = actorId;
        }

        public string ActorId { get; }

        public int TimeoutCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public long? LastCancelExpectedGeneration { get; private set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            TimeSpan dueTime,
            CancellationToken ct = default)
        {
            _ = triggerEnvelope;
            _ = dueTime;
            ct.ThrowIfCancellationRequested();
            TimeoutCalls++;
            _generation++;
            return Task.FromResult(new RuntimeCallbackLease(ActorId, callbackId, _generation));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            string callbackId,
            EventEnvelope triggerEnvelope,
            TimeSpan dueTime,
            TimeSpan period,
            CancellationToken ct = default)
        {
            _ = triggerEnvelope;
            _ = dueTime;
            _ = period;
            ct.ThrowIfCancellationRequested();
            _generation++;
            return Task.FromResult(new RuntimeCallbackLease(ActorId, callbackId, _generation));
        }

        public Task CancelAsync(
            string callbackId,
            long? expectedGeneration = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            ct.ThrowIfCancellationRequested();
            CancelCalls++;
            LastCancelExpectedGeneration = expectedGeneration;
            return Task.CompletedTask;
        }
    }
}
