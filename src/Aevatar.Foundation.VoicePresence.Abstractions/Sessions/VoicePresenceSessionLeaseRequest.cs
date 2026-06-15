namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host session setup depended on direct access to a local voice module instance.
//   New principle: host session setup is a typed actor command request seeded from the last observed capability snapshot.
public sealed record VoicePresenceSessionLeaseRequest(
    string ActorId,
    string ModuleName,
    string SessionId,
    string OwnerId,
    DateTimeOffset ExpiresAtUtc,
    long ObservedStateVersion,
    VoiceRemoteAudioSupport ObservedRemoteAudioSupport,
    VoiceSessionOverrides? SessionOverrides = null);
