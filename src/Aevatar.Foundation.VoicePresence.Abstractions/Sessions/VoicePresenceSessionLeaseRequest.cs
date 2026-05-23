namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public sealed record VoicePresenceSessionLeaseRequest(
    string ActorId,
    string ModuleName,
    string SessionId,
    string OwnerId,
    DateTimeOffset ExpiresAtUtc);
