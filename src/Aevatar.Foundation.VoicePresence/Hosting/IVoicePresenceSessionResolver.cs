namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Resolves one actor-scoped voice session for host transports.
/// </summary>
public interface IVoicePresenceSessionResolver
{
    Task<VoicePresenceSession?> ResolveAsync(string actorId, CancellationToken ct = default);
}
