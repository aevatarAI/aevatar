using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;
using Aevatar.Workflow.Core;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeCallbackSchedulerStateProtoTests
{
    [Fact]
    public void RuntimeCallbackSchedulerState_ShouldRoundtripTypedScheduleContract()
    {
        var state = new RuntimeCallbackSchedulerState
        {
            PendingReminderUnregistrations = { "cb-pending" },
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
                    NextDueAtUnixTimeMs = 1_780_000_000_000,
                    OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
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
        callback.NextDueAtUnixTimeMs.Should().Be(1_780_000_000_000);
        callback.OverduePolicy.Should().Be(RuntimeCallbackOverduePolicy.Deliver);
        roundTripped.PendingReminderUnregistrations.Should().ContainSingle()
            .Which.Should().Be("cb-pending");
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
    public void RuntimeCallbackSchedulerGrain_ShouldVerifyOnlyExactPersistedFleetReconcileEnvelope()
    {
        const long generation = 7;
        const long fireIndex = 3;
        var trigger = new EventEnvelope
        {
            Id = "fleet-reconcile-trigger",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                TopologyAudience.Self),
            Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
        };
        var delivered = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            generation,
            fireIndex,
            trigger,
            RuntimeCallbackDeliveryMode.FiredSelfEvent,
            RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        var scheduled = new RuntimeScheduledCallback
        {
            ActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            CallbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            Generation = generation,
            SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
            PendingDeliveryEnvelope = delivered.Clone(),
        };
        var state = new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                [RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId] = scheduled,
            },
        };

        RuntimeCallbackSchedulerGrain
            .IsExactRuntimeFleetReconcileDelivery(state, delivered.Clone())
            .Should().BeTrue();

        var forgedWithPersistedIdentity = delivered.Clone();
        forgedWithPersistedIdentity.Route.PublisherActorId = "forged-publisher";
        RuntimeCallbackSchedulerGrain
            .IsExactRuntimeFleetReconcileDelivery(state, forgedWithPersistedIdentity)
            .Should().BeFalse();

        scheduled.LastDeliveryEnvelope = delivered.Clone();
        scheduled.PendingDeliveryEnvelope = null;
        RuntimeCallbackSchedulerGrain
            .IsExactRuntimeFleetReconcileDelivery(state, delivered.Clone())
            .Should().BeTrue();
    }

    [Fact]
    public void RuntimeCallbackSchedulerStateStorageSerializer_ShouldRoundtripAnyValueBytes()
    {
        var state = new RuntimeCallbackSchedulerState
        {
            ReminderCallbacks =
            {
                ["cb-byte-string"] = new RuntimeScheduledCallback
                {
                    ActorId = "actor-byte-string",
                    CallbackId = "cb-byte-string",
                    Generation = 9,
                    SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
                    DueTimeMillis = 1000,
                    DeliveryMode = RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
                    TriggerEnvelope = CreateEnvelopeWithAnyPayload("evt-byte-string", new Any
                    {
                        TypeUrl = "type.googleapis.com/aevatar.test.ByteStringPayload",
                        Value = ByteString.CopyFrom(0x01, 0x02, 0x03, 0x7F),
                    }),
                    NextDueAtUnixTimeMs = 1_780_000_000_000,
                    OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
                },
            },
        };
        var serializer = new RuntimeCallbackSchedulerStateGrainStorageSerializer();

        var serialized = serializer.Serialize(state);
        var roundTripped = serializer.Deserialize<RuntimeCallbackSchedulerState>(serialized);

        var payload = roundTripped.ReminderCallbacks["cb-byte-string"].TriggerEnvelope.Payload;
        payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.test.ByteStringPayload");
        payload.Value.ToByteArray().Should().Equal(0x01, 0x02, 0x03, 0x7F);
        payload.Value.Should().NotBeEmpty();
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
            NextDueAtUnixTimeMs = 1_780_000_000_000,
            OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
        };
        await persistentState.WriteStateAsync();
        await persistentState.ReadStateAsync();

        proxy.WriteCount.Should().Be(1);
        proxy.ReadCount.Should().Be(1);
        persistentState.State.ReminderCallbacks.Should().ContainKey("cb-2");
        persistentState.State.ReminderCallbacks["cb-2"].TriggerEnvelope.Id.Should().Be("evt-2");
        persistentState.State.ReminderCallbacks["cb-2"].NextDueAtUnixTimeMs.Should().Be(1_780_000_000_000);
        persistentState.State.ReminderCallbacks["cb-2"].OverduePolicy.Should().Be(RuntimeCallbackOverduePolicy.Deliver);
    }

    [Fact]
    public void RuntimeCallbackSchedulerGrain_ShouldResetLegacyPersistentStateSlot()
    {
        var constructor = typeof(RuntimeCallbackSchedulerGrain)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Subject;
        var parameter = constructor.GetParameters()
            .Should()
            .ContainSingle(candidate =>
                candidate.ParameterType ==
                typeof(IPersistentState<RuntimeCallbackSchedulerState>))
            .Subject;
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
        var parameter = constructor.GetParameters()
            .Should()
            .ContainSingle(candidate =>
                candidate.ParameterType ==
                typeof(IPersistentState<RuntimeCallbackSchedulerState>))
            .Subject;

        parameter.ParameterType.Should().Be(typeof(IPersistentState<RuntimeCallbackSchedulerState>));
        var attribute = parameter.GetCustomAttribute<PersistentStateAttribute>();
        attribute.Should().NotBeNull();
        attribute!.StateName.Should().Be("runtime-callback-scheduler-v2");
        attribute.StorageName.Should().Be(OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName);
        attribute.StorageName.Should().NotBe(OrleansRuntimeConstants.GrainStateStorageName);
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
        var grain = CreateGrain(persistentState);

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
        var grain = CreateGrain(persistentState);

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
        var grain = CreateGrain(persistentState);

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
        var grain = CreateGrain(persistentState);

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
    public async Task RuntimeCallbackSchedulerGrain_ShouldRejectCredentialEnvelopeBeforePersistingTypedState()
    {
        var persistentState =
            DispatchProxy.Create<IPersistentState<RuntimeCallbackSchedulerState>, RuntimeCallbackPersistentStateProxy>();
        var grain = CreateGrain(persistentState);
        var envelope = CreateEnvelope(
            "evt-credential",
            new NeedsCredentialPayload
            {
                ReplyToken = "runtime-reply-token",
            });

        var act = () => grain.ScheduleTimeoutAsync("credential-callback", envelope, dueTimeMs: 100);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*reply_token*");
        persistentState.State.ReminderCallbacks.Should().BeEmpty();
        ((RuntimeCallbackPersistentStateProxy)(object)persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public void DurableCallbackEnvelopeCredentialGuard_ShouldWalkNestedRepeatedAndMapMessages()
    {
        var repeatedPayload = new EventStoreCommitResult
        {
            CommittedEvents =
            {
                new StateEvent
                {
                    EventData = Any.Pack(new NeedsCredentialPayload
                    {
                        ReplyToken = "runtime-reply-token",
                    }),
                },
            },
        };
        var mapPayload = new WorkflowRunState
        {
            PendingChildRunIdsByParentRunId =
            {
                ["parent"] = new WorkflowRunState.Types.ChildRunIdSet
                {
                    ChildRunIds = { "child-1" },
                },
            },
            ExecutionStates =
            {
                ["credential"] = Any.Pack(new NeedsCredentialPayload
                {
                    ReplyToken = "runtime-reply-token",
                }),
            },
        };

        var repeatedAct = () => DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(
            CreateEnvelope("evt-repeated", repeatedPayload));
        var mapAct = () => DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(
            CreateEnvelope("evt-map", mapPayload));

        repeatedAct.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*committed_events[0].event_data*aevatar.foundation.runtime.hosting.tests.NeedsCredentialPayload*.reply_token*");
        mapAct.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*execution_states[credential]*aevatar.foundation.runtime.hosting.tests.NeedsCredentialPayload*.reply_token*");
    }

    [Fact]
    public void DurableCallbackEnvelopeCredentialGuard_ShouldAcceptActorOwnedCallbackIdentifiersAndRejectCredential()
    {
        IMessage[] actorOwnedCallbacks =
        [
            WithStringField(new WorkflowToolCallAttemptCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                ExecutionId = "execution-1",
                CallId = "call-1",
                Attempt = 1,
                Success = new WorkflowToolCallAttemptSuccessOutcome { ResultJson = "{}" },
            }, 9, "continuation-1"),
            WithStringField(new WorkflowToolCallTimeoutFiredEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                ExecutionId = "execution-1",
                CallId = "call-1",
                TimeoutMs = 1_000,
            }, 6, "continuation-1"),
            WithStringField(new WorkflowToolCallRetryFiredEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                ExecutionId = "execution-1",
                CallId = "call-1",
                Attempt = 2,
            }, 6, "continuation-1"),
            WithStringField(new WorkflowToolCallExecutionRecoveryFiredEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                ExecutionId = "execution-1",
                CallId = "call-1",
                Attempt = 1,
            }, 6, "continuation-1"),
            WithStringField(new WorkflowLeaseExpirationFiredEvent
            {
                LeaseKey = "lease-1",
                Generation = 1,
                ExpiresAtUnixMs = 1_780_000_000_000,
            }, 2, "holder-fence-1"),
        ];

        foreach (var callback in actorOwnedCallbacks)
        {
            var callbackAct = () => DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(
                CreateEnvelope($"evt-{callback.Descriptor.Name}", callback));

            callbackAct.Should().NotThrow(
                $"{callback.Descriptor.Name} carries stable actor-owned callback identifiers, not runtime credentials");
        }

        var credentialAct = () => DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(
            CreateEnvelope("evt-credential", new NeedsCredentialPayload
            {
                ReplyToken = "runtime-reply-token",
            }));

        credentialAct.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*reply_token*");
    }

    private static T WithStringField<T>(T message, int fieldNumber, string value)
        where T : IMessage
    {
        var field = message.Descriptor.FindFieldByNumber(fieldNumber)
            ?? throw new InvalidOperationException(
                $"{message.Descriptor.FullName} does not define field {fieldNumber}.");
        field.Accessor.SetValue(message, value);
        return message;
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

    private static EventEnvelope CreateEnvelope(string id) =>
        CreateEnvelope(id, new StringValue { Value = "payload" });

    private static EventEnvelope CreateEnvelope(string id, IMessage payload) => new()
    {
        Id = id,
        Payload = Any.Pack(payload),
        Route = EnvelopeRouteSemantics.CreateDirect("actor-1", "actor-1"),
    };

    private static EventEnvelope CreateEnvelopeWithAnyPayload(string id, Any payload) => new()
    {
        Id = id,
        Payload = payload.Clone(),
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
        NextDueAtUnixTimeMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
    };

    // Constructed outside a silo: the grain has no Orleans execution context, so any reminder
    // interaction fails fast. These tests only cover the state-machine and validation paths that
    // run before the grain reaches its reminders.
    private static RuntimeCallbackSchedulerGrain CreateGrain(
        IPersistentState<RuntimeCallbackSchedulerState> persistentState) =>
        new(persistentState);

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

public sealed partial class NeedsCredentialPayload : IMessage<NeedsCredentialPayload>
{
    private static readonly MessageParser<NeedsCredentialPayload> MessageParser =
        new(() => new NeedsCredentialPayload());

    public static MessageParser<NeedsCredentialPayload> Parser => MessageParser;

    public static MessageDescriptor Descriptor =>
        NeedsCredentialPayloadReflection.Descriptor.MessageTypes[0];

    MessageDescriptor IMessage.Descriptor => Descriptor;

    public string ReplyToken { get; set; } = string.Empty;

    public NeedsCredentialPayload()
    {
    }

    public NeedsCredentialPayload(NeedsCredentialPayload other)
    {
        ReplyToken = other.ReplyToken;
    }

    public NeedsCredentialPayload Clone() => new(this);

    public bool Equals(NeedsCredentialPayload? other) =>
        other is not null && string.Equals(ReplyToken, other.ReplyToken, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as NeedsCredentialPayload);

    public override int GetHashCode() => ReplyToken.GetHashCode(StringComparison.Ordinal);

    public void WriteTo(CodedOutputStream output)
    {
        if (ReplyToken.Length != 0)
        {
            output.WriteRawTag(10);
            output.WriteString(ReplyToken);
        }
    }

    public int CalculateSize() =>
        ReplyToken.Length == 0 ? 0 : 1 + CodedOutputStream.ComputeStringSize(ReplyToken);

    public void MergeFrom(NeedsCredentialPayload other)
    {
        if (other.ReplyToken.Length != 0)
            ReplyToken = other.ReplyToken;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 10)
                ReplyToken = input.ReadString();
            else
                input.SkipLastField();
        }
    }
}

public static class NeedsCredentialPayloadReflection
{
    private static readonly FileDescriptor FileDescriptor = FileDescriptor.FromGeneratedCode(
        Convert.FromBase64String(
            "CiN0ZXN0X25lZWRzX2NyZWRlbnRpYWxfcGF5bG9hZC5wcm90bxIoYWV2YXRhci5mb3VuZGF0aW9uLnJ1bnRpbWUuaG9zdGluZy50ZXN0cyItChZOZWVkc0NyZWRlbnRpYWxQYXlsb2FkEhMKC3JlcGx5X3Rva2VuGAEgASgJYgZwcm90bzM="),
        [],
        new GeneratedClrTypeInfo(
            null,
            null,
            [new GeneratedClrTypeInfo(typeof(NeedsCredentialPayload), NeedsCredentialPayload.Parser, ["ReplyToken"], null, null, null, null)]));

    public static FileDescriptor Descriptor => FileDescriptor;
}
