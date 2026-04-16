namespace Aevatar.Foundation.VoicePresence.Transport.Internal;

internal interface IWebRtcVoicePeer : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? OnAudioPacketReceived;
    event Action<string>? OnControlMessageReceived;
    event Action? OnClosed;

    ValueTask SendAudioAsync(ReadOnlyMemory<byte> encodedFrame, uint durationRtpUnits, CancellationToken ct);

    ValueTask SendControlAsync(string controlJson, CancellationToken ct);
}
