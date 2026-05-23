using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackSchedulerStateProtoTests
{
    [Fact]
    public void RuntimeCallbackSchedulerState_ShouldRoundtripTypedScheduleContract()
    {
        var state = new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                ["cb-1"] = new RuntimeScheduledCallback
                {
                    ActorId = "actor-1",
                    CallbackId = "cb-1",
                    Generation = 7,
                    SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
                    Periodic = true,
                    DueTimeMillis = 125,
                    PeriodMillis = 250,
                    FireIndex = 3,
                    DeliveryMode = RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery,
                    TriggerEnvelope = CreateEnvelope("evt-1"),
                },
            },
        };

        var roundTripped = RuntimeCallbackSchedulerState.Parser.ParseFrom(state.ToByteArray());

        roundTripped.ReminderCallbacks.Should().ContainKey("cb-1");
        var callback = roundTripped.ReminderCallbacks["cb-1"];
        callback.ActorId.Should().Be("actor-1");
        callback.CallbackId.Should().Be("cb-1");
        callback.Generation.Should().Be(7);
        callback.SlotEpoch.Should().Be(RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        callback.Periodic.Should().BeTrue();
        callback.DueTimeMillis.Should().Be(125);
        callback.PeriodMillis.Should().Be(250);
        callback.FireIndex.Should().Be(3);
        callback.DeliveryMode.Should().Be(RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery);
        callback.TriggerEnvelope.Id.Should().Be("evt-1");
        callback.TriggerEnvelope.Payload.Unpack<StringValue>().Value.Should().Be("payload");
    }

    [Fact]
    public void RuntimeCallbackSchedulerState_ShouldUseGeneratedProtobufMessage()
    {
        typeof(RuntimeCallbackSchedulerState)
            .Should().BeAssignableTo<IMessage<RuntimeCallbackSchedulerState>>();
        typeof(RuntimeScheduledCallback)
            .Should().BeAssignableTo<IMessage<RuntimeScheduledCallback>>();
    }

    [Fact]
    public async Task RuntimeCallbackSchedulerState_ShouldReadAndWriteThroughOrleansPersistentState()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        var proxy = (RuntimeCallbackPersistentStateProxy)(object)persistentState;

        persistentState.State.ReminderCallbacks["cb-2"] = new RuntimeScheduledCallback
        {
            ActorId = "actor-2",
            CallbackId = "cb-2",
            Generation = 2,
            DueTimeMillis = 1000,
            DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
            TriggerEnvelope = CreateEnvelope("evt-2"),
        };
        await persistentState.WriteStateAsync();
        await persistentState.ReadStateAsync();

        proxy.WriteCount.Should().Be(1);
        proxy.ReadCount.Should().Be(1);
        persistentState.State.ReminderCallbacks.Should().ContainKey("cb-2");
        persistentState.State.ReminderCallbacks["cb-2"].TriggerEnvelope.Id.Should().Be("evt-2");
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrain_ShouldResetLegacyPersistentStateSlot()
    {
        var constructor = typeof(RuntimeCallbackSchedulerGrain)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Subject;
        var parameter = constructor.GetParameters().Should().ContainSingle().Subject;
        var attribute = parameter.GetCustomAttribute<PersistentStateAttribute>();

        attribute.Should().NotBeNull();
        attribute!.StateName.Should().NotBe("runtime-callback-scheduler");
        attribute.StateName.Should().Be("runtime-callback-scheduler-v2");
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrain_ShouldReadAndWriteGeneratedProtoStateSlot()
    {
        var constructor = typeof(RuntimeCallbackSchedulerGrain)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Subject;
        var parameter = constructor.GetParameters().Should().ContainSingle().Subject;

        parameter.ParameterType.Should().Be(typeof(IPersistentState<RuntimeCallbackSchedulerState>));
        var attribute = parameter.GetCustomAttribute<PersistentStateAttribute>();
        attribute.Should().NotBeNull();
        attribute!.StateName.Should().Be("runtime-callback-scheduler-v2");
    }

    [Fact]
    public async Task RuntimeCallbackSchedulerGrain_ShouldNotCancelV2ScheduleWithOldEpochLease()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        persistentState.State.ReminderCallbacks["cb-1"] = new RuntimeScheduledCallback
        {
            ActorId = "actor-1",
            CallbackId = "cb-1",
            Generation = 1,
            SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
            DueTimeMillis = 1000,
            DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
            TriggerEnvelope = CreateEnvelope("evt-1"),
        };
        var proxy = (RuntimeCallbackPersistentStateProxy)(object)persistentState;
        var grain = new RuntimeCallbackSchedulerGrain(persistentState);

        await grain.CancelAsync(
            "cb-1",
            expectedGeneration: 1,
            expectedSlotEpoch: RuntimeCallbackSlotEpoch.Unspecified);

        persistentState.State.ReminderCallbacks.Should().ContainKey("cb-1");
        proxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeCallbackSchedulerGrain_ShouldIgnoreCancelWhenGenerationDoesNotMatch()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        persistentState.State.ReminderCallbacks["cb-1"] = CreateScheduledCallback("cb-1", generation: 2);
        var proxy = (RuntimeCallbackPersistentStateProxy)(object)persistentState;
        var grain = new RuntimeCallbackSchedulerGrain(persistentState);

        await grain.CancelAsync(
            "cb-1",
            expectedGeneration: 1,
            expectedSlotEpoch: RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        persistentState.State.ReminderCallbacks.Should().ContainKey("cb-1");
        proxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task RuntimeCallbackSchedulerGrain_ShouldRemoveScheduleWhenEpochAndGenerationMatch()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        persistentState.State.ReminderCallbacks["cb-1"] = CreateScheduledCallback("cb-1", generation: 1);
        var grain = new RuntimeCallbackSchedulerGrain(persistentState);

        var act = () => grain.CancelAsync(
            "cb-1",
            expectedGeneration: 1,
            expectedSlotEpoch: RuntimeCallbackSlotEpoch.OrleansSchedulerV2);

        await act.Should().ThrowAsync<ArgumentNullException>();
        persistentState.State.ReminderCallbacks.Should().BeEmpty();
        ((RuntimeCallbackPersistentStateProxy)(object)persistentState).WriteCount.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeCallbackSchedulerGrain_ShouldValidateTimerPeriodBeforePersistingTypedState()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        var grain = new RuntimeCallbackSchedulerGrain(persistentState);

        var act = () => grain.ScheduleTimerAsync(
            "timer-callback",
            CreateEnvelope("evt-timer"),
            dueTimeMs: 100,
            periodMs: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        persistentState.State.ReminderCallbacks.Should().BeEmpty();
        ((RuntimeCallbackPersistentStateProxy)(object)persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrainBoundary_ShouldAcceptTypedEventEnvelope()
    {
        var scheduleTimeout = typeof(IRuntimeCallbackSchedulerGrain).GetMethod(
            nameof(IRuntimeCallbackSchedulerGrain.ScheduleTimeoutAsync));
        var scheduleTimer = typeof(IRuntimeCallbackSchedulerGrain).GetMethod(
            nameof(IRuntimeCallbackSchedulerGrain.ScheduleTimerAsync));

        scheduleTimeout.Should().NotBeNull();
        scheduleTimeout!.GetParameters()[1].ParameterType.Should().Be(typeof(EventEnvelope));
        scheduleTimer.Should().NotBeNull();
        scheduleTimer!.GetParameters()[1].ParameterType.Should().Be(typeof(EventEnvelope));
    }

    [Theory]
    [InlineData(RuntimeCallbackDeliveryMode.FiredSelfEvent, RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent)]
    [InlineData(RuntimeCallbackDeliveryMode.EnvelopeRedelivery, RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery)]
    public void RuntimeCallbackSchedulerGrain_ShouldMapDeliveryModeToTypedProto(
        RuntimeCallbackDeliveryMode runtimeMode,
        RuntimeCallbackScheduleDeliveryMode protoMode)
    {
        var method = typeof(RuntimeCallbackSchedulerGrain).GetMethod(
            "ToProtoDeliveryMode",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.Invoke(null, [runtimeMode]).Should().Be(protoMode);
    }

    [Theory]
    [InlineData(RuntimeCallbackScheduleDeliveryMode.Unspecified, RuntimeCallbackDeliveryMode.FiredSelfEvent)]
    [InlineData(RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent, RuntimeCallbackDeliveryMode.FiredSelfEvent)]
    [InlineData(RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery, RuntimeCallbackDeliveryMode.EnvelopeRedelivery)]
    public void RuntimeCallbackSchedulerGrain_ShouldMapTypedProtoDeliveryModeToRuntime(
        RuntimeCallbackScheduleDeliveryMode protoMode,
        RuntimeCallbackDeliveryMode runtimeMode)
    {
        var method = typeof(RuntimeCallbackSchedulerGrain).GetMethod(
            "FromProtoDeliveryMode",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.Invoke(null, [protoMode]).Should().Be(runtimeMode);
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrain_ShouldRejectUnknownRuntimeDeliveryMode()
    {
        var method = typeof(RuntimeCallbackSchedulerGrain).GetMethod(
            "ToProtoDeliveryMode",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var act = () => method!.Invoke(null, [(RuntimeCallbackDeliveryMode)999]);

        act.Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrain_ShouldRejectUnknownPersistedDeliveryMode()
    {
        var method = typeof(RuntimeCallbackSchedulerGrain).GetMethod(
            "FromProtoDeliveryMode",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var act = () => method!.Invoke(null, [(RuntimeCallbackScheduleDeliveryMode)999]);

        act.Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<ArgumentOutOfRangeException>();
    }

    private static EventEnvelope CreateEnvelope(string id) => new()
    {
        Id = id,
        Payload = Any.Pack(new StringValue { Value = "payload" }),
        Route = EnvelopeRouteSemantics.CreateDirect("actor-1", "actor-1"),
    };

    private static RuntimeScheduledCallback CreateScheduledCallback(string callbackId, long generation) => new()
    {
        ActorId = "actor-1",
        CallbackId = callbackId,
        Generation = generation,
        SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        DueTimeMillis = 1000,
        DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
        TriggerEnvelope = CreateEnvelope("evt-1"),
    };

    private class RuntimeCallbackPersistentStateProxy : DispatchProxy
    {
        public RuntimeCallbackSchedulerState State { get; set; } = new();

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_State" => State,
                "set_State" => SetState(args),
                "ReadStateAsync" => CountRead(),
                "WriteStateAsync" => CountWrite(),
                "ClearStateAsync" => Task.CompletedTask,
                "get_RecordExists" => true,
                "get_Etag" => string.Empty,
                "set_Etag" => null,
                _ => GetDefault(targetMethod?.ReturnType),
            };
        }

        private object? SetState(object?[]? args)
        {
            State = args?[0] as RuntimeCallbackSchedulerState ?? new RuntimeCallbackSchedulerState();
            return null;
        }

        private Task CountRead()
        {
            ReadCount++;
            return Task.CompletedTask;
        }

        private Task CountWrite()
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        private static object? GetDefault(System.Type? type)
        {
            if (type == null || type == typeof(void))
                return null;

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
