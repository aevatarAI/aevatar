namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Host-side request used to resolve one actor-scoped voice session.
/// </summary>
public sealed record VoicePresenceSessionRequest(
    string ActorId,
    string? ModuleName = null,
    VoicePresenceSessionRequestPurpose Purpose = VoicePresenceSessionRequestPurpose.Attach);

public enum VoicePresenceSessionRequestPurpose
{
    Attach = 0,
    Detach = 1,
}
