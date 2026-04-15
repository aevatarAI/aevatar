namespace Aevatar.Foundation.VoicePresence.Abstractions;

/// <summary>
/// Raw audio frame delivered through the voice fast path.
/// </summary>
public readonly record struct VoiceAudioFastPathFrame(
    string LinkId,
    ReadOnlyMemory<byte> Pcm16,
    DateTimeOffset ReceivedAt);
