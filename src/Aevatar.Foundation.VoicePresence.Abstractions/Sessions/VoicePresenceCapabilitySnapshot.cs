namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host code treated a local VoicePresenceModule instance as the session capability snapshot.
//   New principle: session capability is a typed read-model snapshot with actor-owned version and transport facts.
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
    VoiceRemoteAudioSupport RemoteAudioSupport,
    string? ActiveTransportLeaseId = null,
    long LeaseEpoch = 0,
    string? ActiveLeaseOwnerId = null);
