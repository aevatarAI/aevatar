namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public sealed record VoicePresenceCapabilitySnapshot(
    string ActorId,
    string ModuleName,
    long StateVersion,
    string LastEventId,
    DateTimeOffset UpdatedAt,
    bool Initialized,
    bool TransportAttached,
    int PcmSampleRateHz,
    string? ActiveSessionId,
    DateTimeOffset? LeaseExpiresAt,
    VoiceRemoteAudioSupport RemoteAudioSupport);
