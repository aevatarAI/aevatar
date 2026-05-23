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
    private readonly IVoicePresenceCapabilityQueryPort _capabilityQueryPort;

    public VoicePresenceSessionLeasePort(
        IActorDispatchPort dispatchPort,
        IVoicePresenceCapabilityQueryPort capabilityQueryPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _capabilityQueryPort = capabilityQueryPort ?? throw new ArgumentNullException(nameof(capabilityQueryPort));
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

        var observed = await _capabilityQueryPort.GetAsync(request.ActorId, request.ModuleName, ct);
        if (observed?.ActiveSessionId != request.SessionId)
        {
            throw new InvalidOperationException("Voice session lease was not observed in the capability read model.");
        }

        return new VoicePresenceSessionLeaseHandle(
            observed.ActorId,
            observed.ModuleName,
            request.SessionId,
            request.OwnerId,
            observed.StateVersion,
            observed.LeaseExpiresAt ?? expiresAtUtc,
            observed.RemoteAudioSupport);
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
}
