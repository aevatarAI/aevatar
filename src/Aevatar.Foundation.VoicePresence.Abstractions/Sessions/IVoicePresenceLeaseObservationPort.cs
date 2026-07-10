namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public interface IVoicePresenceLeaseObservationPort
{
    Task<VoicePresenceCapabilitySnapshot> ObserveSessionLeaseAsync(
        VoicePresenceSessionLeaseRequest request,
        CancellationToken ct = default);

    Task<VoicePresenceCapabilitySnapshot> ObserveTransportAttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        string transportLeaseId,
        CancellationToken ct = default);
}
