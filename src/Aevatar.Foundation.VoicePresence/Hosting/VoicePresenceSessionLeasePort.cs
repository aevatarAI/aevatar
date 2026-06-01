using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Hosting;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
//   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
public sealed class VoicePresenceSessionLeasePort : IVoicePresenceSessionLeasePort
{
    private readonly IActorDispatchPort _dispatchPort;

    public VoicePresenceSessionLeasePort(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
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
                }),
            ct);

        return new VoicePresenceSessionLeaseHandle(
            request.ActorId,
            request.ModuleName,
            request.SessionId,
            request.OwnerId,
            request.ObservedStateVersion,
            expiresAtUtc,
            request.ObservedRemoteAudioSupport);
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
                    Reason = reason,
                }),
            ct);
    }
}
