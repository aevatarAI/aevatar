using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class ActorOwnedVoiceRealtimeSession
    : IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>
{
    internal static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);
    private const string HostOwnerId = "voice-presence.host";

    private readonly IVoicePresenceCapabilityQueryPort _capabilityQueryPort;
    private readonly IVoicePresenceSessionLeasePort _leasePort;
    private readonly IVoiceVolatileMediaStreamPort _mediaStreamPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActorOwnedVoiceRealtimeSession>? _logger;

    public ActorOwnedVoiceRealtimeSession(
        IVoicePresenceCapabilityQueryPort capabilityQueryPort,
        IVoicePresenceSessionLeasePort leasePort,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        TimeProvider? timeProvider = null,
        ILogger<ActorOwnedVoiceRealtimeSession>? logger = null)
    {
        _capabilityQueryPort = capabilityQueryPort ?? throw new ArgumentNullException(nameof(capabilityQueryPort));
        _leasePort = leasePort ?? throw new ArgumentNullException(nameof(leasePort));
        _mediaStreamPort = mediaStreamPort ?? throw new ArgumentNullException(nameof(mediaStreamPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
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
            if (inbound.Purpose == VoiceRealtimeSessionPurpose.Detach)
            {
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

            // Attach over an existing/stale transport: TAKE IT OVER instead of rejecting with 409.
            // The gate above reads an eventually-consistent capability PROJECTION; a stale projection
            // (Garnet write-skip / version gap) would make a projection re-read fail-close every retry
            // forever. So we deliberately do NOT re-read the projection and fail-close here. We dispatch
            // the SessionId-only lease release (which clears the grain even on a TTL-expired / relay-dead
            // lease) and fall through to AcquireAsync. The GRAIN is the strongly-consistent authority:
            // HandleSessionLeaseRequestedAsync grants only when no DIFFERENT session holds the lease (a
            // genuinely-live peer makes the lease observation time out rather than double-attaching), and
            // every grant bumps LeaseEpoch so the new session out-fences the old relay's late frames
            // (IsAcceptedTransportSignal enforces the epoch match).
            if (!string.IsNullOrWhiteSpace(capability.ActiveSessionId))
            {
                var evictHandle = new VoicePresenceSessionLeaseHandle(
                    capability.ActorId,
                    capability.ModuleName,
                    capability.ActiveSessionId,
                    HostOwnerId,
                    capability.StateVersion,
                    capability.LeaseExpiresAt ?? _timeProvider.GetUtcNow(),
                    capability.RemoteAudioSupport,
                    capability.ActiveTransportLeaseId,
                    capability.LeaseEpoch,
                    inbound.ToolContext?.Clone());
                await _mediaStreamPort.DetachAsync(evictHandle, expectedTransport: null, ct);
                _logger?.LogInformation(
                    "voice takeover: evicted prior session {EvictedSessionId} (transportLease={TransportLeaseId}, epoch={LeaseEpoch}) on actor {ActorId}/{ModuleName}; proceeding to acquire",
                    capability.ActiveSessionId,
                    capability.ActiveTransportLeaseId,
                    capability.LeaseEpoch,
                    capability.ActorId,
                    capability.ModuleName);
            }
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
