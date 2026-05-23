using System.Runtime.CompilerServices;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoiceTransportRelayTests
{
    [Fact]
    public async Task User_audio_should_dispatch_transport_audio_signal_without_provider_send_before_actor_turn()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.Audio(new byte[] { 10, 20, 30 }),
            VoiceTransportFrame.Audio(new byte[] { 40, 50 }),
        ]);

        var dispatched = new List<IMessage>();
        module.AttachTransport(transport, (message, _) =>
        {
            dispatched.Add(message);
            return Task.CompletedTask;
        });

        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        provider.AudioFrames.ShouldBeEmpty();
        var audioSignals = dispatched.OfType<VoiceTransportAudioFrameReceived>().ToArray();
        audioSignals.Length.ShouldBe(2);
        audioSignals[0].Pcm16.ToByteArray().ShouldBe([10, 20, 30]);
        audioSignals[1].Pcm16.ToByteArray().ShouldBe([40, 50]);
        audioSignals.All(static x => x.SampleRateHz == 24000).ShouldBeTrue();
    }

    [Fact]
    public async Task User_audio_actor_turn_should_send_provider_once_for_current_lease()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(roleAgent);
        var leaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveLeaseOwnerId = "host-1",
            LeaseExpiresAt = leaseExpiresAt.Clone(),
            Status = VoicePresenceRuntimeStatus.Idle,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.Audio(new byte[] { 10, 20, 30 }),
        ]);

        await module.AttachTransportAsync(transport, async (message, ct) =>
        {
            if (message is VoiceTransportAttachRequested attach)
            {
                attach.SessionId.ShouldBe("lease-1");
                attach.OwnerId.ShouldBe("host-1");
                attach.TransportLeaseId.ShouldNotBeNullOrWhiteSpace();
                attach.LeaseExpiresAt.ShouldBe(leaseExpiresAt);

                await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
                {
                    ModuleName = "voice_presence",
                    TransportAttachRequested = attach,
                }), ctx, ct);
                return;
            }

            if (message is VoiceTransportAudioFrameReceived audio)
            {
                audio.SessionId.ShouldBe("lease-1");
                audio.OwnerId.ShouldBe("host-1");
                audio.TransportLeaseId.ShouldBe(roleAgent.State.VoicePresence["voice_presence"].ActiveTransportLeaseId);
                audio.LeaseExpiresAt.ShouldBe(leaseExpiresAt);
                audio.Pcm16.ToByteArray().ShouldBe([10, 20, 30]);
                audio.SampleRateHz.ShouldBe(24000);

                await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
                {
                    ModuleName = "voice_presence",
                    TransportAudioFrameReceived = audio,
                }), ctx, ct);
                return;
            }

            throw new InvalidOperationException($"Unexpected self signal {message.GetType().Name}.");
        }, "lease-1", "host-1", leaseExpiresAt.Clone());
        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        provider.AudioFrames.ShouldHaveSingleItem().ShouldBe([10, 20, 30]);
        roleAgent.State.VoicePresence["voice_presence"].TransportAttached.ShouldBeTrue();
    }

    [Fact]
    public async Task User_control_frame_should_update_state_machine()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(roleAgent);

        module.StateMachine.AllocateNextResponseId();
        module.StateMachine.OnResponseDone(module.StateMachine.CurrentResponseId);
        var responseId = module.StateMachine.CurrentResponseId;
        module.StateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);

        var drainAck = new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = responseId,
                PlayoutSequence = 42,
            },
        };

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.ControlFrame(drainAck),
        ]);

        const string transportLeaseId = "transport-1";
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveLeaseOwnerId = "host-1",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            TransportAttached = true,
            ActiveTransportLeaseId = transportLeaseId,
            Status = VoicePresenceRuntimeStatus.AudioDraining,
            CurrentResponseId = drainAck.DrainAcknowledged.ResponseId,
            NextResponseId = drainAck.DrainAcknowledged.ResponseId + 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        module.AttachTransport(transport, (message, ct) =>
        {
            if (message is VoiceTransportAttachRequested)
                return Task.CompletedTask;

            if (message is VoiceTransportControlFrameReceived control)
            {
                control.OwnerId = "host-1";
                control.TransportLeaseId = transportLeaseId;
                control.LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone();
                return module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
                {
                    ModuleName = "voice_presence",
                    TransportControlFrameReceived = control,
                }), ctx, ct);
            }

            return module.HandleAsync(CreateEnvelope(message), ctx, ct);
        }, "lease-1", "host-1", Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)));
        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        var persistedState = roleAgent.State.VoicePresence["voice_presence"];
        persistedState.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        persistedState.LastDrainAckResponseId.ShouldBe(responseId);
    }

    [Fact]
    public async Task Provider_audio_should_not_relay_until_transport_attach_is_actor_accepted()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        var dispatchedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.AttachTransport(transport, (_, _) =>
        {
            dispatchedSignal.TrySetResult();
            return Task.CompletedTask;
        });

        var audioEvent = new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived
            {
                Pcm16 = ByteString.CopyFrom([1, 2, 3]),
                SampleRateHz = 24000,
            },
        };

        await provider.SimulateEventAndWait(audioEvent, dispatchedSignal);

        transport.SentAudio.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_control_event_should_dispatch_to_grain()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        var dispatchedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new List<IMessage>();
        module.AttachTransport(transport, (message, _) =>
        {
            dispatched.Add(message);
            dispatchedSignal.TrySetResult();
            return Task.CompletedTask;
        });

        await provider.SimulateEventAndWait(
            new VoiceProviderEvent { SpeechStarted = new VoiceSpeechStarted() },
            dispatchedSignal);

        dispatched.Count.ShouldBe(1);
        dispatched[0].ShouldBeOfType<VoiceProviderEventReceived>()
            .ProviderEvent.EventCase.ShouldBe(VoiceProviderEvent.EventOneofCase.SpeechStarted);
        transport.SentAudio.ShouldBeEmpty();
    }

    [Fact]
    public async Task DetachTransport_should_stop_relay()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        module.AttachTransport(transport, (_, _) => Task.CompletedTask);
        module.HasVolatileTransportLease.ShouldBeTrue();

        await module.DetachTransportAsync();
        module.HasVolatileTransportLease.ShouldBeFalse();
    }

    [Fact]
    public void Double_attach_should_throw()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);

        var transport1 = new FakeVoiceTransport([]);
        var transport2 = new FakeVoiceTransport([]);
        module.AttachTransport(transport1, (_, _) => Task.CompletedTask);

        Should.Throw<InvalidOperationException>(() =>
            module.AttachTransport(transport2, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Empty_audio_frames_should_be_skipped()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.Audio(ReadOnlyMemory<byte>.Empty),
            VoiceTransportFrame.Audio(new byte[] { 1 }),
        ]);

        module.AttachTransport(transport, (_, _) => Task.CompletedTask);
        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        provider.AudioFrames.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispose_should_stop_relay_and_cleanup()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        module.AttachTransport(transport, (_, _) => Task.CompletedTask);

        await module.DisposeAsync();

        module.IsInitialized.ShouldBeFalse();
        module.HasVolatileTransportLease.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
        provider.Disposed.ShouldBeTrue();
    }

    private static VoicePresenceModule CreateModule(RecordingProvider provider) =>
        new(
            provider,
            new VoiceProviderConfig
            {
                ProviderName = "openai",
                Endpoint = "wss://test",
                ApiKey = "sk-test",
                Model = "gpt-realtime",
            },
            new VoiceSessionConfig
            {
                Voice = "alloy",
                Instructions = "test",
                SampleRateHz = 24000,
            },
            logger: NullLogger.Instance);

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };

    // ── Test doubles ──────────────────────────────────────────

    private sealed class RecordingProvider : IRealtimeVoiceProvider
    {
        private Func<VoiceProviderEvent, CancellationToken, Task>? _onEvent;

        public int ConnectCalls { get; private set; }
        public int UpdateSessionCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool Disposed { get; private set; }
        public List<byte[]> AudioFrames { get; } = [];
        public List<VoiceConversationEventInjection> InjectedEvents { get; } = [];

        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent
        {
            set => _onEvent = value;
        }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct) { ConnectCalls++; return Task.CompletedTask; }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) { AudioFrames.Add(pcm16.ToArray()); return Task.CompletedTask; }
        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) => Task.CompletedTask;
        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) { InjectedEvents.Add(injection.Clone()); return Task.CompletedTask; }
        public Task CancelResponseAsync(CancellationToken ct) { CancelCalls++; return Task.CompletedTask; }
        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) { UpdateSessionCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public async Task SimulateEventAndWait(VoiceProviderEvent evt, TaskCompletionSource signal)
        {
            if (_onEvent != null)
                await _onEvent(evt, CancellationToken.None);
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class FakeVoiceTransport : IVoiceTransport
    {
        private readonly IReadOnlyList<VoiceTransportFrame> _frames;
        private readonly TaskCompletionSource _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeVoiceTransport(IReadOnlyList<VoiceTransportFrame> frames)
        {
            _frames = frames;
        }

        public List<ReadOnlyMemory<byte>> SentAudio { get; } = [];
        public List<VoiceControlFrame> SentControl { get; } = [];
        public bool Disposed { get; private set; }

        public TaskCompletionSource AudioSentSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            SentAudio.Add(pcm16);
            AudioSentSignal.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            SentControl.Add(frame);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var frame in _frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }

            _consumed.TrySetResult();

            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
        }

        public Task WaitUntilConsumed(TimeSpan timeout) =>
            _consumed.Task.WaitAsync(timeout);

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class StubEventHandlerContext(Aevatar.Foundation.Abstractions.IAgent? agent = null) : Aevatar.Foundation.Abstractions.EventModules.IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();
        public string AgentId => "voice-agent";
        public IServiceProvider Services { get; } = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public Aevatar.Foundation.Abstractions.IAgent Agent { get; } = agent ?? new StubAgent();

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = audience;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(
            Aevatar.Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgent : Aevatar.Foundation.Abstractions.IAgent
    {
        public string Id => "voice-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("voice-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleAgent(string id) : Aevatar.Foundation.Abstractions.IAgent, IVoicePresenceRuntimeStateOwner
    {
        public string Id => id;

        public RecordingRoleState State { get; } = new();

        public bool TryGetVoicePresenceRuntimeState(string moduleName, out VoicePresenceRuntimeState runtimeState)
        {
            if (State.VoicePresence.TryGetValue(moduleName, out var stored))
            {
                runtimeState = stored.Clone();
                return true;
            }

            runtimeState = new VoicePresenceRuntimeState();
            return false;
        }

        public Task PersistVoicePresenceRuntimeStateAsync(
            string moduleName,
            VoicePresenceRuntimeState runtimeState,
            CancellationToken ct = default)
        {
            _ = ct;
            State.VoicePresence[moduleName] = runtimeState.Clone();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(id);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleState
    {
        public Dictionary<string, VoicePresenceRuntimeState> VoicePresence { get; } = [];
    }
}
