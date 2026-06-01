namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: a resolved local module reference doubled as the host's attachment authority.
//   New principle: a lease handle carries the stable actor/module/session identity accepted for actor dispatch.
public sealed record VoicePresenceSessionLeaseHandle(
    string ActorId,
    string ModuleName,
    string SessionId,
    string OwnerId,
    long ObservedStateVersion,
    DateTimeOffset ExpiresAtUtc,
    VoiceRemoteAudioSupport RemoteAudioSupport,
    string? ActiveTransportLeaseId = null);
