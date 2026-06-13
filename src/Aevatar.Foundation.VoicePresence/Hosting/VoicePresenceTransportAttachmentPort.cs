using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoicePresenceTransportAttachmentPort(IActorDispatchPort dispatchPort)
    : IVoicePresenceTransportAttachmentPort
{
    private readonly IActorDispatchPort _dispatchPort =
        dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));

    public async Task<VoicePresenceSessionLeaseHandle> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(transport);

        var transportLeaseId = Guid.NewGuid().ToString("N");
        var attachedHandle = handle with
        {
            ActiveTransportLeaseId = transportLeaseId,
        };

        await _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceTransportAttachRequested
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = transportLeaseId,
                    LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                }),
            ct);

        return attachedHandle;
    }

    public Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _ = expectedTransport;

        var transportLeaseId = handle.ActiveTransportLeaseId;
        if (string.IsNullOrWhiteSpace(transportLeaseId))
            return Task.CompletedTask;

        return _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceTransportDetachRequested
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = transportLeaseId,
                    LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                    Reason = "host_transport_detached",
                }),
            ct);
    }
}
