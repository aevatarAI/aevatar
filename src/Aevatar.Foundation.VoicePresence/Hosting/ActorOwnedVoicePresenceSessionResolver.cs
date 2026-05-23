using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
//   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
public sealed class ActorOwnedVoicePresenceSessionResolver : IVoicePresenceSessionResolver
{
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);
    private const string HostOwnerId = "voice-presence.host";

    private readonly IVoicePresenceCapabilityQueryPort _capabilityQueryPort;
    private readonly IVoicePresenceSessionLeasePort _leasePort;
    private readonly IVoicePresenceTransportAttachmentPort _transportAttachmentPort;
    private readonly TimeProvider _timeProvider;

    public ActorOwnedVoicePresenceSessionResolver(
        IVoicePresenceCapabilityQueryPort capabilityQueryPort,
        IVoicePresenceSessionLeasePort leasePort,
        IVoicePresenceTransportAttachmentPort transportAttachmentPort,
        TimeProvider? timeProvider = null)
    {
        _capabilityQueryPort = capabilityQueryPort ?? throw new ArgumentNullException(nameof(capabilityQueryPort));
        _leasePort = leasePort ?? throw new ArgumentNullException(nameof(leasePort));
        _transportAttachmentPort = transportAttachmentPort ?? throw new ArgumentNullException(nameof(transportAttachmentPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<VoicePresenceSession?> ResolveAsync(
        VoicePresenceSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);

        var capability = await _capabilityQueryPort.GetAsync(request.ActorId, request.ModuleName, ct);
        if (capability == null)
            return null;

        var leaseRequest = new VoicePresenceSessionLeaseRequest(
            capability.ActorId,
            capability.ModuleName,
            Guid.NewGuid().ToString("N"),
            HostOwnerId,
            _timeProvider.GetUtcNow().Add(DefaultLeaseTtl));

        var leaseHandle = await _leasePort.AcquireAsync(leaseRequest, ct);
        return new VoicePresenceSession(
            capability,
            leaseHandle,
            _leasePort,
            _transportAttachmentPort);
    }
}
