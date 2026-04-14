using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceModuleTests
{
    [Fact]
    public async Task Initialize_and_audio_fast_path_should_forward_provider_calls()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, linkId: "user-audio");

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAudioAsync(
            new VoiceAudioFastPathFrame("user-audio", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await module.DisposeAsync();

        provider.ConnectCalls.ShouldBe(1);
        provider.UpdateSessionCalls.ShouldBe(1);
        provider.AudioFrames.Single().ShouldBe(new byte[] { 1, 2, 3 });
        provider.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_should_only_accept_voice_provider_and_control_frames()
    {
        var module = CreateModule(new RecordingVoiceProvider());

        module.CanHandle(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        })).ShouldBeTrue();

        module.CanHandle(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged { ResponseId = 1, PlayoutSequence = 7 },
        })).ShouldBeTrue();

        module.CanHandle(CreateEnvelope(new StringValue { Value = "not-voice" })).ShouldBeFalse();
    }

    [Fact]
    public async Task Speech_started_during_response_should_cancel_provider_and_switch_to_user_speaking()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);

        provider.CancelCalls.ShouldBe(1);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
        module.StateMachine.CurrentResponseId.ShouldBe(1);
    }

    [Fact]
    public async Task Response_done_and_drain_ack_should_release_injection_fence()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 2 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ResponseId = 2 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 2,
                PlayoutSequence = 88,
            },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
        module.StateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public async Task Response_done_should_transition_to_audio_draining()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);
    }

    [Fact]
    public async Task Response_cancelled_should_return_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Speech_stopped_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStopped = new VoiceSpeechStopped(),
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
    }

    [Fact]
    public async Task Provider_disconnected_should_reset_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected { Reason = "test" },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Noop_provider_events_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived { Pcm16 = Google.Protobuf.ByteString.CopyFrom([1, 2]), SampleRateHz = 24000 },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested { CallId = "c1", ToolName = "t", ArgumentsJson = "{}", ResponseId = 1 },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Error = new VoiceProviderError { ErrorCode = "e", ErrorMessage = "msg" },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Null_payload_should_be_ignored()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        }, ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public void HandleAudio_wrong_link_should_throw()
    {
        var module = CreateModule(new RecordingVoiceProvider(), linkId: "link-a");

        Should.Throw<InvalidOperationException>(() =>
            module.HandleAudioAsync(
                new VoiceAudioFastPathFrame("link-b", new byte[] { 1 }, DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public void CanHandleAudio_empty_linkId_matches_any()
    {
        var module = CreateModule(new RecordingVoiceProvider(), linkId: null);

        module.CanHandleAudio(new VoiceAudioFastPathFrame("any-link", new byte[] { 1 }, DateTimeOffset.UtcNow))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_should_dispose_provider()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);

        await module.InitializeAsync(CancellationToken.None);
        module.IsInitialized.ShouldBeTrue();

        await module.DisposeAsync();
        module.IsInitialized.ShouldBeFalse();
        provider.Disposed.ShouldBeTrue();
    }

    private static VoicePresenceModule CreateModule(RecordingVoiceProvider provider, string? linkId = null)
    {
        return new VoicePresenceModule(
            provider,
            new VoiceProviderConfig
            {
                ProviderName = "openai",
                Endpoint = "wss://example.test/realtime",
                ApiKey = "sk-test",
                Model = "gpt-realtime",
            },
            new VoiceSessionConfig
            {
                Voice = "alloy",
                Instructions = "be concise",
                SampleRateHz = 24000,
                ToolNames = { "doorbell.open" },
            },
            new VoicePresenceModuleOptions
            {
                LinkId = linkId,
            });
    }

    private static EventEnvelope CreateEnvelope(IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };
    }

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
    {
        public int ConnectCalls { get; private set; }

        public int UpdateSessionCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public bool Disposed { get; private set; }

        public List<byte[]> AudioFrames { get; } = [];

        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
        {
            _ = config;
            _ = ct;
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = ct;
            AudioFrames.Add(pcm16.ToArray());
            return Task.CompletedTask;
        }

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = callId;
            _ = resultJson;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            CancelCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = session;
            _ = ct;
            UpdateSessionCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "voice-agent";

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IAgent Agent { get; } = new StubAgent();

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

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "voice-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("voice-agent");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
