namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host voice session resolution inferred capability from local runtime object shape.
//   New principle: host session resolution reads actor-owned voice capability facts from the current-state read model.
public interface IVoicePresenceCapabilityQueryPort
{
    Task<VoicePresenceCapabilitySnapshot?> GetAsync(
        string actorId,
        string? moduleName,
        CancellationToken ct = default);
}
