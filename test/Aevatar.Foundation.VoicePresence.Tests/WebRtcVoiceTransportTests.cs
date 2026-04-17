using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Google.Protobuf;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class WebRtcVoiceTransportTests
{
    [Fact]
    public async Task SendAudioAsync_should_buffer_until_full_frame_and_send_opus_packet()
    {
        var peer = new FakeWebRtcVoicePeer();
        var options = new WebRtcVoiceTransportOptions
        {
            PcmSampleRateHz = 24000,
            FrameDurationMs = 20,
        };
        await using var transport = new WebRtcVoiceTransport(peer, options);
        var codec = new OpusPcmCodec(options.PcmSampleRateHz, options.FrameDurationMs);
        var pcmFrame = new byte[codec.PcmBytesPerFrame];

        await transport.SendAudioAsync(pcmFrame[..128], CancellationToken.None);
        peer.AudioSends.Count.ShouldBe(0);

        await transport.SendAudioAsync(pcmFrame[128..], CancellationToken.None);

        peer.AudioSends.Count.ShouldBe(1);
        peer.AudioSends[0].DurationRtpUnits.ShouldBe(codec.OpusRtpDurationUnitsPerFrame);
        peer.AudioSends[0].Payload.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SendControlAsync_should_serialize_control_json()
    {
        var peer = new FakeWebRtcVoicePeer();
        await using var transport = new WebRtcVoiceTransport(peer, new WebRtcVoiceTransportOptions());

        await transport.SendControlAsync(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 7,
                PlayoutSequence = 42,
            },
        }, CancellationToken.None);

        peer.ControlSends.Count.ShouldBe(1);
        var parsed = JsonParser.Default.Parse<VoiceControlFrame>(peer.ControlSends[0]);
        parsed.DrainAcknowledged.ResponseId.ShouldBe(7);
        parsed.DrainAcknowledged.PlayoutSequence.ShouldBe(42);
    }

    [Fact]
    public async Task ReceiveFramesAsync_should_decode_audio_and_control_and_complete_on_close()
    {
        var peer = new FakeWebRtcVoicePeer();
        var options = new WebRtcVoiceTransportOptions
        {
            PcmSampleRateHz = 24000,
            FrameDurationMs = 20,
        };
        await using var transport = new WebRtcVoiceTransport(peer, options);
        var codec = new OpusPcmCodec(options.PcmSampleRateHz, options.FrameDurationMs);

        var receiveTask = CollectFramesAsync(transport);

        peer.EmitAudio(codec.EncodePacket(new byte[codec.PcmBytesPerFrame]));
        peer.EmitControl(JsonFormatter.Default.Format(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 9,
                PlayoutSequence = 88,
            },
        }));
        peer.EmitClosed();

        var frames = await receiveTask;
        frames.Count.ShouldBe(2);
        frames[0].IsAudio.ShouldBeTrue();
        frames[0].AudioPcm16.Length.ShouldBe(codec.PcmBytesPerFrame);
        frames[1].IsAudio.ShouldBeFalse();
        frames[1].Control.ShouldNotBeNull();
        frames[1].Control!.DrainAcknowledged.ResponseId.ShouldBe(9);
    }

    [Fact]
    public async Task DisposeAsync_should_dispose_peer_and_reject_further_sends()
    {
        var peer = new FakeWebRtcVoicePeer();
        var transport = new WebRtcVoiceTransport(peer, new WebRtcVoiceTransportOptions());

        await transport.DisposeAsync();

        peer.Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            transport.SendAudioAsync(new byte[] { 1 }, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            transport.SendControlAsync(new VoiceControlFrame(), CancellationToken.None));
    }

    private static async Task<List<VoiceTransportFrame>> CollectFramesAsync(WebRtcVoiceTransport transport)
    {
        var frames = new List<VoiceTransportFrame>();
        await foreach (var frame in transport.ReceiveFramesAsync(CancellationToken.None))
            frames.Add(frame);

        return frames;
    }

    private sealed class FakeWebRtcVoicePeer : IWebRtcVoicePeer
    {
        public event Action<ReadOnlyMemory<byte>>? OnAudioPacketReceived;
        public event Action<string>? OnControlMessageReceived;
        public event Action? OnClosed;

        public bool Disposed { get; private set; }

        public List<(byte[] Payload, uint DurationRtpUnits)> AudioSends { get; } = [];

        public List<string> ControlSends { get; } = [];

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> encodedFrame, uint durationRtpUnits, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AudioSends.Add((encodedFrame.ToArray(), durationRtpUnits));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendControlAsync(string controlJson, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ControlSends.Add(controlJson);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            OnClosed?.Invoke();
            return ValueTask.CompletedTask;
        }

        public void EmitAudio(byte[] payload) => OnAudioPacketReceived?.Invoke(payload);

        public void EmitControl(string payload) => OnControlMessageReceived?.Invoke(payload);

        public void EmitClosed() => OnClosed?.Invoke();
    }
}
