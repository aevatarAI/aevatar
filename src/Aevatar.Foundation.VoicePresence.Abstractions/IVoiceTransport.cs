namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// User-side voice transport. Implementations expose raw media frames; the owner
/// module decides whether to forward them from its actor turn.
/// </summary>
public interface IVoiceTransport : IAsyncDisposable
{
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);

    Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct);

    IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(CancellationToken ct);
}

/// <summary>
/// A single frame received from the user-side voice transport.
/// Binary WebSocket frames become audio; text frames become control.
/// </summary>
public readonly record struct VoiceTransportFrame
{
    public bool IsAudio { get; init; }
    public ReadOnlyMemory<byte> AudioPcm16 { get; init; }
    public VoiceControlFrame? Control { get; init; }

    public static VoiceTransportFrame Audio(ReadOnlyMemory<byte> pcm16) =>
        new() { IsAudio = true, AudioPcm16 = pcm16 };

    public static VoiceTransportFrame ControlFrame(VoiceControlFrame control) =>
        new() { IsAudio = false, Control = control };
}
