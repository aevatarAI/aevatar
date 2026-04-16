namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Fast path for raw audio transport frames that should bypass the event pipeline.
/// </summary>
public interface IAudioFastPath
{
    /// <summary>
    /// Returns whether the audio frame belongs to this fast-path handler.
    /// </summary>
    bool CanHandleAudio(VoiceAudioFastPathFrame frame);

    /// <summary>
    /// Handles raw PCM16 audio without wrapping it into an event envelope.
    /// </summary>
    Task HandleAudioAsync(VoiceAudioFastPathFrame frame, CancellationToken ct);
}
