using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

// Refactor (iter51/issue-888-voice-presence-lease-ack-snapshot):
//   Old pattern: lease ACK returned VoicePresenceSession bound to pre-lease capability snapshot; endpoint accept/reject closed over stale transport facts.
//   New principle: lease ACK only signals inbox receipt; attach readiness is a separate signal; resolver preflights capability and returns typed sentinel (Unsupported/PreflightFailed/PendingAttach/Attached); endpoint maps typed sentinel, not boolean closure.
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

    public async Task<VoicePresenceSessionResolution> ResolveAsync(
        VoicePresenceSessionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);

        var capability = await _capabilityQueryPort.GetAsync(request.ActorId, request.ModuleName, ct);
        if (capability == null)
            return VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotFound);

        if (!capability.Initialized)
        {
            return VoicePresenceSessionResolution.PreflightFailed(
                VoicePresencePreflightFailureKind.NotInitialized,
                capability.StateVersion);
        }

        if (capability.TransportAttached || IsActive(capability.LeaseExpiresAt, capability.ActiveSessionId))
        {
            if (request.Purpose == VoicePresenceSessionRequestPurpose.Detach)
            {
                var attachedSession = VoicePresenceSession.CreateAttachedForDetach(
                    capability,
                    new VoicePresenceSessionLeaseHandle(
                        capability.ActorId,
                        capability.ModuleName,
                        capability.ActiveSessionId ?? string.Empty,
                        HostOwnerId,
                        capability.StateVersion,
                        capability.LeaseExpiresAt ?? _timeProvider.GetUtcNow(),
                        capability.RemoteAudioSupport),
                    _leasePort,
                    _transportAttachmentPort);

                return VoicePresenceSessionResolution.LeaseAcceptedAttached(
                    attachedSession,
                    capability.StateVersion);
            }

            return VoicePresenceSessionResolution.PreflightFailed(
                VoicePresencePreflightFailureKind.TransportAlreadyAttached,
                capability.StateVersion);
        }

        if (capability.RemoteAudioSupport != VoiceRemoteAudioSupport.Supported ||
            _transportAttachmentPort is UnavailableVoicePresenceTransportAttachmentPort)
        {
            return VoicePresenceSessionResolution.Unsupported(capability.StateVersion);
        }

        var leaseRequest = new VoicePresenceSessionLeaseRequest(
            capability.ActorId,
            capability.ModuleName,
            Guid.NewGuid().ToString("N"),
            HostOwnerId,
            _timeProvider.GetUtcNow().Add(DefaultLeaseTtl),
            capability.StateVersion,
            capability.RemoteAudioSupport);

        var leaseHandle = await _leasePort.AcquireAsync(leaseRequest, ct);
        var session = new VoicePresenceSession(
            capability,
            leaseHandle,
            _leasePort,
            _transportAttachmentPort);
        return VoicePresenceSessionResolution.LeaseAcceptedPendingAttach(
            session,
            leaseHandle.ObservedStateVersion);
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private bool IsActive(DateTimeOffset? expiresAtUtc, string? activeSessionId) =>
        !string.IsNullOrWhiteSpace(activeSessionId) &&
        expiresAtUtc.HasValue &&
        expiresAtUtc.Value.ToUniversalTime() > UtcNow;
}
