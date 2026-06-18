using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoicePresenceSessionLeasePort : IVoicePresenceSessionLeasePort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IVoicePresenceLeaseObservationPort _observationPort;

    public VoicePresenceSessionLeasePort(
        IActorDispatchPort dispatchPort,
        IVoicePresenceLeaseObservationPort observationPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _observationPort = observationPort ?? throw new ArgumentNullException(nameof(observationPort));
    }

    public async Task<VoicePresenceSessionLeaseHandle> AcquireAsync(
        VoicePresenceSessionLeaseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModuleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OwnerId);

        var expiresAtUtc = request.ExpiresAtUtc.ToUniversalTime();
        await _dispatchPort.DispatchAsync(
            request.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                request.ActorId,
                request.ModuleName,
                new VoicePresenceSessionLeaseRequested
                {
                    SessionId = request.SessionId,
                    OwnerId = request.OwnerId,
                    ExpiresAt = Timestamp.FromDateTimeOffset(expiresAtUtc),
                    SessionOverrides = request.SessionOverrides?.Clone(),
                    ToolContext = request.ToolContext?.Clone(),
                }),
            ct);

        var observed = await _observationPort.ObserveSessionLeaseAsync(request, ct);

        return new VoicePresenceSessionLeaseHandle(
            request.ActorId,
            request.ModuleName,
            request.SessionId,
            request.OwnerId,
            observed.StateVersion,
            observed.LeaseExpiresAt?.ToUniversalTime() ?? expiresAtUtc,
            observed.RemoteAudioSupport,
            observed.ActiveTransportLeaseId,
            observed.LeaseEpoch,
            request.ToolContext?.Clone(),
            // Carry the resolved per-session prompt so the host-side relay session can apply it.
            request.SessionOverrides?.Instructions);
    }

    public Task ReleaseAsync(
        VoicePresenceSessionLeaseHandle handle,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoicePresenceSessionLeaseReleased
                {
                    SessionId = handle.SessionId,
                    Reason = reason,
                }),
            ct);
    }

    public Task CompleteTransportLifetimeAsync(
        VoicePresenceSessionLeaseHandle handle,
        string transportLeaseId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportLeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return _dispatchPort.DispatchAsync(
            handle.ActorId,
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                handle.ActorId,
                handle.ModuleName,
                new VoiceTransportLifetimeCompleted
                {
                    SessionId = handle.SessionId,
                    OwnerId = handle.OwnerId,
                    TransportLeaseId = transportLeaseId,
                    LeaseExpiresAt = Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
                    LeaseEpoch = handle.LeaseEpoch,
                    Reason = reason,
                }),
            ct);
    }
}
