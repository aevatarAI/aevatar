namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public sealed record VoicePresenceSessionLeaseHandle(
    string ActorId,
    string ModuleName,
    string SessionId,
    string OwnerId,
    long StateVersion,
    DateTimeOffset ExpiresAtUtc,
    VoiceRemoteAudioSupport RemoteAudioSupport);
