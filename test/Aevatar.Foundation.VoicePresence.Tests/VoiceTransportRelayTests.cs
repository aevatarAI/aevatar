using System.Runtime.CompilerServices;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoiceTransportRelayTests
{
    [Fact]
    public async Task User_audio_should_relay_directly_to_provider()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.Audio(new byte[] { 10, 20, 30 }),
            VoiceTransportFrame.Audio(new byte[] { 40, 50 }),
        ]);

        var dispatched = new List<VoiceProviderEvent>();
        module.AttachTransport(transport, (evt, _) =>
        {
            dispatched.Add(evt);
            return Task.CompletedTask;
        });

        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        provider.AudioFrames.Count.ShouldBe(2);
        provider.AudioFrames[0].ShouldBe([10, 20, 30]);
        provider.AudioFrames[1].ShouldBe([40, 50]);
        dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task User_control_frame_should_update_state_machine()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        module.StateMachine.AllocateNextResponseId();
        module.StateMachine.OnResponseDone(module.StateMachine.CurrentResponseId);
        module.StateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);

        var drainAck = new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = module.StateMachine.CurrentResponseId,
                PlayoutSequence = 42,
            },
        };

        var transport = new FakeVoiceTransport([
            VoiceTransportFrame.ControlFrame(drainAck),
        ]);

        module.AttachTransport(transport, (_, _) => Task.CompletedTask);
        await transport.WaitUntilConsumed(TimeSpan.FromSeconds(3));

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
        module.StateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public async Task Provider_audio_should_relay_directly_to_user_transport()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        module.AttachTransport(transport, (_, _) => Task.CompletedTask);

        var audioEvent = new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived
            {
                Pcm16 = ByteString.CopyFrom([1, 2, 3]),
                SampleRateHz = 24000,
            },
        };

        await provider.SimulateEventAndWait(audioEvent, transport.AudioSentSignal);

        transport.SentAudio.Count.ShouldBe(1);
        transport.SentAudio[0].ToArray().ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Provider_control_event_should_dispatch_to_grain()
    {
        var provider = new RecordingProvider();
        var module = CreateModule(provider);
        await module.InitializeAsync(CancellationToken.None);

        var transport = new FakeVoiceTransport([]);
        var dispatchedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = new List<VoiceProviderEvent>();
        module.AttachTransport(transport, (evt, _) =>
        {
            dispatched.Add(evt);
            dispatchedSignal.TrySetResult();
            return Task.CompletedTask;
        });

        await provider.SimulateEventAndWait(
            new VoiceProviderEvent { SpeechStarted = new VoiceSpeechStarted() },
            dispatchedSignal);

        dispatched.Count.ShouldBe(1);
        dispatched[0].EventCase.ShouldBe(VoiceProviderEvent.EventOneofCase.SpeechStarted);
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
        module.IsTransportAttached.ShouldBeTrue();

        await module.DetachTransportAsync();
        module.IsTransportAttached.ShouldBeFalse();
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

        provider.AudioFrames.Count.ShouldBe(1);
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
        module.IsTransportAttached.ShouldBeFalse();
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

    // ── Test doubles ──────────────────────────────────────────

    private sealed class RecordingProvider : IRealtimeVoiceProvider
    {
        private Func<VoiceProviderEvent, CancellationToken, Task>? _onEvent;

        public int ConnectCalls { get; private set; }
        public int UpdateSessionCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool Disposed { get; private set; }
        public List<byte[]> AudioFrames { get; } = [];

        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent
        {
            set => _onEvent = value;
        }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct) { ConnectCalls++; return Task.CompletedTask; }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) { AudioFrames.Add(pcm16.ToArray()); return Task.CompletedTask; }
        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) => Task.CompletedTask;
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
}
