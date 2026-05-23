namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public interface IVoicePresenceSessionLeasePort
{
    Task<VoicePresenceSessionLeaseHandle> AcquireAsync(
        VoicePresenceSessionLeaseRequest request,
        CancellationToken ct = default);

    Task ReleaseAsync(
        VoicePresenceSessionLeaseHandle handle,
        string reason,
        CancellationToken ct = default);
}
