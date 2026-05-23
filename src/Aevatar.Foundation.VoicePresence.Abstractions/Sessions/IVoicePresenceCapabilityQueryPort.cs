namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public interface IVoicePresenceCapabilityQueryPort
{
    Task<VoicePresenceCapabilitySnapshot?> GetAsync(
        string actorId,
        string? moduleName,
        CancellationToken ct = default);
}
