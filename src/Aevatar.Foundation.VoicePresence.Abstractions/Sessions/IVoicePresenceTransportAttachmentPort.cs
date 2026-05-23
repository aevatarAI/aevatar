namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

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
