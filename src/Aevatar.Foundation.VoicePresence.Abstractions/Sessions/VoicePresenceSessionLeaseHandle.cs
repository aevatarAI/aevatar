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
    string? ActiveTransportLeaseId = null,
    long LeaseEpoch = 0,
    VoiceToolExecutionContext? ToolContext = null,
    // The resolved per-session system prompt (chat-route policy sessionOverrides.instructions).
    // Carried on the handle so the host-side relay/model session can apply it — the relay is built
    // from static app config and otherwise never sees the policy prompt.
    string? Instructions = null);
