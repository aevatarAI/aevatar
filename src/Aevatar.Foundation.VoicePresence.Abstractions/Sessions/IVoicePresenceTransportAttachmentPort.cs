namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: transports attached directly to a process-local voice module found by inspecting the actor instance.
//   New principle: transports attach through an explicit lease handle so host code does not own actor/session facts.
public interface IVoicePresenceTransportAttachmentPort
{
    Task AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default);

    Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default);
}
