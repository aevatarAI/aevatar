using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class ActorOwnedVoiceRealtimeSession
    : IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>
{
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);
    private const string HostOwnerId = "voice-presence.host";

    private readonly IVoicePresenceCapabilityQueryPort _capabilityQueryPort;
    private readonly IVoicePresenceSessionLeasePort _leasePort;
    private readonly IVoiceVolatileMediaStreamPort _mediaStreamPort;
    private readonly TimeProvider _timeProvider;

    public ActorOwnedVoiceRealtimeSession(
        IVoicePresenceCapabilityQueryPort capabilityQueryPort,
        IVoicePresenceSessionLeasePort leasePort,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        TimeProvider? timeProvider = null)
    {
        _capabilityQueryPort = capabilityQueryPort ?? throw new ArgumentNullException(nameof(capabilityQueryPort));
        _leasePort = leasePort ?? throw new ArgumentNullException(nameof(leasePort));
        _mediaStreamPort = mediaStreamPort ?? throw new ArgumentNullException(nameof(mediaStreamPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>> ExecuteAsync(
        VoiceRealtimeSessionRequest inbound,
        Func<VoiceRealtimeFrame, CancellationToken, ValueTask> emitAsync,
        Func<VoiceRealtimeSessionAccepted, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(emitAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(inbound.ActorId);

        var capability = await _capabilityQueryPort.GetAsync(inbound.ActorId, inbound.ModuleName, ct);
        if (capability == null)
            return Failure(VoiceRealtimeSessionStartError.NotFound);

        if (!capability.Initialized)
            return Failure(VoiceRealtimeSessionStartError.NotInitialized);

        if (capability.TransportAttached || IsActive(capability.LeaseExpiresAt, capability.ActiveSessionId))
        {
            if (inbound.Purpose != VoiceRealtimeSessionPurpose.Detach)
                return Failure(VoiceRealtimeSessionStartError.TransportAlreadyAttached);

            var acceptedDetach = BuildAccepted(
                capability,
                new VoicePresenceSessionLeaseHandle(
                    capability.ActorId,
                    capability.ModuleName,
                    capability.ActiveSessionId ?? string.Empty,
                    HostOwnerId,
                    capability.StateVersion,
                    capability.LeaseExpiresAt ?? _timeProvider.GetUtcNow(),
                    capability.RemoteAudioSupport,
                    capability.ActiveTransportLeaseId,
                    capability.LeaseEpoch,
                    inbound.ToolContext?.Clone()));
            if (onAcceptedAsync != null)
                await onAcceptedAsync(acceptedDetach, ct);

            return RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
                .Success(acceptedDetach, VoiceRealtimeSessionCompletion.Accepted, completed: true);
        }

        if (inbound.Purpose == VoiceRealtimeSessionPurpose.Detach)
            return Failure(VoiceRealtimeSessionStartError.NotFound);

        if (capability.RemoteAudioSupport != VoiceRemoteAudioSupport.Supported ||
            !_mediaStreamPort.SupportsRemoteAudio)
        {
            return Failure(VoiceRealtimeSessionStartError.Unsupported);
        }

        var leaseRequest = new VoicePresenceSessionLeaseRequest(
            capability.ActorId,
            capability.ModuleName,
            Guid.NewGuid().ToString("N"),
            HostOwnerId,
            _timeProvider.GetUtcNow().Add(DefaultLeaseTtl),
            capability.StateVersion,
            capability.RemoteAudioSupport,
            inbound.SessionOverrides?.Clone(),
            inbound.ToolContext?.Clone());

        var leaseHandle = await _leasePort.AcquireAsync(leaseRequest, ct);
        var accepted = BuildAccepted(capability, leaseHandle);
        if (onAcceptedAsync != null)
            await onAcceptedAsync(accepted, ct);

        return RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
            .Success(accepted, VoiceRealtimeSessionCompletion.Accepted, completed: true);
    }

    private static RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion> Failure(
        VoiceRealtimeSessionStartError error) =>
        RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>
            .Failure(error);

    private static VoiceRealtimeSessionAccepted BuildAccepted(
        VoicePresenceCapabilitySnapshot capability,
        VoicePresenceSessionLeaseHandle leaseHandle) =>
        new(
            capability.ActorId,
            capability.ModuleName,
            leaseHandle.SessionId,
            capability.PcmSampleRateHz,
            leaseHandle.ObservedStateVersion,
            leaseHandle);

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private bool IsActive(DateTimeOffset? expiresAtUtc, string? activeSessionId) =>
        !string.IsNullOrWhiteSpace(activeSessionId) &&
        expiresAtUtc.HasValue &&
        expiresAtUtc.Value.ToUniversalTime() > UtcNow;
}
