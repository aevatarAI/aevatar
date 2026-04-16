using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Google.Protobuf;

namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// WebRTC implementation of <see cref="IVoiceTransport" />.
/// Audio is carried over RTP/Opus and control frames are carried over a data channel.
/// </summary>
public sealed class WebRtcVoiceTransport : IVoiceTransport
{
    private static readonly JsonFormatter ControlJsonWriter = new(JsonFormatter.Settings.Default);
    private static readonly JsonParser ControlJsonReader = new(JsonParser.Settings.Default);

    private readonly IWebRtcVoicePeer _peer;
    private readonly OpusPcmCodec _codec;
    private readonly Channel<VoiceTransportFrame> _frames;
    private readonly List<byte> _pendingSendPcm = [];
    private bool _disposed;

    internal WebRtcVoiceTransport(
        IWebRtcVoicePeer peer,
        WebRtcVoiceTransportOptions options)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        ArgumentNullException.ThrowIfNull(options);

        _codec = new OpusPcmCodec(options.PcmSampleRateHz, options.FrameDurationMs);
        _frames = Channel.CreateBounded<VoiceTransportFrame>(new BoundedChannelOptions(options.PendingSendFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });

        _peer.OnAudioPacketReceived += HandleIncomingAudioPacket;
        _peer.OnControlMessageReceived += HandleIncomingControlMessage;
        _peer.OnClosed += HandleClosed;
        Completion = _frames.Reader.Completion;
    }

    public Task Completion { get; }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm16.IsEmpty)
            return;

        lock (_pendingSendPcm)
        {
            _pendingSendPcm.AddRange(pcm16.ToArray());
        }

        while (true)
        {
            byte[] frame;
            lock (_pendingSendPcm)
            {
                if (_pendingSendPcm.Count < _codec.PcmBytesPerFrame)
                    break;

                frame = _pendingSendPcm.GetRange(0, _codec.PcmBytesPerFrame).ToArray();
                _pendingSendPcm.RemoveRange(0, _codec.PcmBytesPerFrame);
            }

            var encoded = _codec.EncodePacket(frame);
            await _peer.SendAudioAsync(encoded, _codec.OpusRtpDurationUnitsPerFrame, ct);
        }
    }

    public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        var json = ControlJsonWriter.Format(frame);
        return _peer.SendControlAsync(json, ct).AsTask();
    }

    public IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(CancellationToken ct) =>
        _frames.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frames.Writer.TryComplete();
        await _peer.DisposeAsync();
    }

    private void HandleIncomingAudioPacket(ReadOnlyMemory<byte> encodedPacket)
    {
        if (_disposed || encodedPacket.IsEmpty)
            return;

        try
        {
            var pcm16 = _codec.DecodePacket(encodedPacket.Span);
            _frames.Writer.TryWrite(VoiceTransportFrame.Audio(pcm16));
        }
        catch
        {
            // ignore malformed RTP payloads
        }
    }

    private void HandleIncomingControlMessage(string json)
    {
        if (_disposed || string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var frame = ControlJsonReader.Parse<VoiceControlFrame>(json);
            _frames.Writer.TryWrite(VoiceTransportFrame.ControlFrame(frame));
        }
        catch
        {
            // ignore malformed control payloads
        }
    }

    private void HandleClosed()
    {
        if (_disposed)
            return;

        _frames.Writer.TryComplete();
    }
}
