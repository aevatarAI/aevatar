using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class FailClosedVoiceVolatileMediaStreamPort(
    IVoicePresenceTransportAttachmentPort transportAttachmentPort,
    IVoicePresenceSessionLeasePort leasePort)
    : IVoiceVolatileMediaStreamPort
{
    private const string DetachedReason = "host_transport_detached";

    private readonly IVoicePresenceTransportAttachmentPort _transportAttachmentPort =
        transportAttachmentPort ?? throw new ArgumentNullException(nameof(transportAttachmentPort));
    private readonly IVoicePresenceSessionLeasePort _leasePort =
        leasePort ?? throw new ArgumentNullException(nameof(leasePort));

    public bool SupportsRemoteAudio => false;

    public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(transport);
        throw new VoiceVolatileMediaStreamUnavailableException();
    }

    public async Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await _transportAttachmentPort.DetachAsync(handle, expectedTransport, ct);
        await _leasePort.ReleaseAsync(handle, DetachedReason, ct);
    }

    public async Task CompleteTransportLifetimeAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceTransportLifetimeCompleted? completed,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var transportLeaseId = string.IsNullOrWhiteSpace(completed?.TransportLeaseId)
            ? handle.ActiveTransportLeaseId
            : completed.TransportLeaseId;
        if (string.IsNullOrWhiteSpace(transportLeaseId))
            return;

        await _leasePort.CompleteTransportLifetimeAsync(handle, transportLeaseId, reason, ct);
    }
}
