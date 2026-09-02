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

    // Bounded re-read window after a capability self-heal dispatch (materialization may complete
    // asynchronously). Mirrors the chat-route-policy self-heal in PolicyAwareVoiceEndpoints (commit
    // 7076ad9): max 5 reads, 400ms backoff.
    private const int CapabilityRecoveryMaxReads = 5;
    private static readonly TimeSpan CapabilityRecoveryBackoff = TimeSpan.FromMilliseconds(400);

    private readonly IVoicePresenceCapabilityQueryPort _capabilityQueryPort;
    private readonly IVoicePresenceSessionLeasePort _leasePort;
    private readonly IVoiceVolatileMediaStreamPort _mediaStreamPort;
    private readonly IVoicePresenceCapabilityProjectionRecoveryPort? _capabilityRecoveryPort;
    private readonly IVoicePresenceCapabilityAutoEnablePort? _capabilityAutoEnablePort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActorOwnedVoiceRealtimeSession>? _logger;

    public ActorOwnedVoiceRealtimeSession(
        IVoicePresenceCapabilityQueryPort capabilityQueryPort,
        IVoicePresenceSessionLeasePort leasePort,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        IVoicePresenceCapabilityProjectionRecoveryPort? capabilityRecoveryPort = null,
        IVoicePresenceCapabilityAutoEnablePort? capabilityAutoEnablePort = null,
        TimeProvider? timeProvider = null,
        ILogger<ActorOwnedVoiceRealtimeSession>? logger = null)
    {
        _capabilityQueryPort = capabilityQueryPort ?? throw new ArgumentNullException(nameof(capabilityQueryPort));
        _leasePort = leasePort ?? throw new ArgumentNullException(nameof(leasePort));
        _mediaStreamPort = mediaStreamPort ?? throw new ArgumentNullException(nameof(mediaStreamPort));
        _capabilityRecoveryPort = capabilityRecoveryPort;
        _capabilityAutoEnablePort = capabilityAutoEnablePort;
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

        // Self-heal a stale/missing capability read-model row before failing closed. An auto-provisioned
        // default voice agent commits its capability event to the grain, but the read-model row can be
        // missing (projection lag / long-idle grain), so GetAsync returns null and attach 404s without a
        // manual voice-presence/enable. Re-fire the committed-state activation plan to re-materialize the
        // row from already-committed events (no new commit, no grain mutation), then re-read with a short
        // bounded window. Gated on a null snapshot + an actor that actually has committed events, so the
        // happy path is untouched and a genuinely-unprovisioned actor still 404s. Mirrors the
        // chat-route-policy self-heal in PolicyAwareVoiceEndpoints (commit 7076ad9).
        if (capability == null &&
            _capabilityRecoveryPort != null &&
            await _capabilityRecoveryPort.TryRematerializeAsync(inbound.ActorId, ct))
        {
            for (var attempt = 0; attempt < CapabilityRecoveryMaxReads && capability == null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(CapabilityRecoveryBackoff, _timeProvider, ct);
                capability = await _capabilityQueryPort.GetAsync(inbound.ActorId, inbound.ModuleName, ct);
            }
        }

        // True zero-config: if the capability is STILL null after the re-projection self-heal above, the
        // default voice agent was never enabled at all (its grain holds no committed VoicePresenceEnabled
        // event for the recovery port to re-materialize). Auto-PROVISION it — commit the same enable
        // voice-presence/enable would, then re-read with the same bounded window — so connecting to
        // /ws/voice with no explicit actor enables the caller's default voice capability on first connect
        // and attaches, instead of 404/503-ing forever. Idempotent and scoped to the genuinely-unprovisioned
        // default path: the auto-enable port no-ops unless the resolved actor actually exists with a known,
        // registered runtime agent kind, so a happy path (non-null capability) and a bogus actor are
        // untouched. The enable is read-model-asynchronous, so we re-read — if it has not materialized within
        // the window the existing typed retryable 503 (CapabilityNotReady) below still surfaces on retry.
        if (capability == null &&
            _capabilityAutoEnablePort != null &&
            await _capabilityAutoEnablePort.TryAutoEnableAsync(inbound.ActorId, inbound.ModuleName, ct))
        {
            for (var attempt = 0; attempt < CapabilityRecoveryMaxReads && capability == null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(CapabilityRecoveryBackoff, _timeProvider, ct);
                capability = await _capabilityQueryPort.GetAsync(inbound.ActorId, inbound.ModuleName, ct);
            }

            // The enable committed but the read model has not caught up within the bounded window. This is a
            // retryable materialization lag (the same shape as a lease-observe timeout), not a genuinely
            // missing capability — surface the typed retryable 503 so the client re-attaches rather than
            // getting a permanent 404.
            if (capability == null)
            {
                _logger?.LogInformation(
                    "voice auto-enable committed for actor {ActorId}/{ModuleName} but capability not yet materialized; returning CapabilityNotReady (retryable)",
                    inbound.ActorId,
                    inbound.ModuleName);
                return Failure(VoiceRealtimeSessionStartError.CapabilityNotReady);
            }
        }

        if (capability == null)
            return Failure(VoiceRealtimeSessionStartError.NotFound);

        if (!capability.Initialized)
            return Failure(VoiceRealtimeSessionStartError.NotInitialized);

        var attachOutcome = VoiceRealtimeAttachOutcome.NewSession;
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
                attachOutcome = VoiceRealtimeAttachOutcome.Restarted;
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

        VoicePresenceSessionLeaseHandle leaseHandle;
        try
        {
            leaseHandle = await _leasePort.AcquireAsync(leaseRequest, ct);
        }
        catch (TimeoutException ex)
        {
            // The grain accepted the lease, but the capability read-model projection never reflected the
            // committed lease within the observation budget (projection lag / stuck read model). Surface a
            // typed, retryable CapabilityNotReady (→ 503 voice_capability_not_ready) instead of letting the
            // TimeoutException bubble out of the ingress as an opaque empty-body 500.
            _logger?.LogWarning(
                ex,
                "voice lease acquire did not observe committed capability state for actor {ActorId}/{ModuleName}; returning CapabilityNotReady (retryable)",
                capability.ActorId,
                capability.ModuleName);
            return Failure(VoiceRealtimeSessionStartError.CapabilityNotReady);
        }

        var accepted = BuildAccepted(capability, leaseHandle, attachOutcome);
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
        VoicePresenceSessionLeaseHandle leaseHandle,
        VoiceRealtimeAttachOutcome attachOutcome = VoiceRealtimeAttachOutcome.NewSession) =>
        new(
            capability.ActorId,
            capability.ModuleName,
            leaseHandle.SessionId,
            capability.PcmSampleRateHz,
            leaseHandle.ObservedStateVersion,
            leaseHandle,
            VoiceWireContractDefaults.CurrentWireContractVersion,
            VoiceWireContractDefaults.CreateInputImagePolicy(),
            attachOutcome);

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    private bool IsActive(DateTimeOffset? expiresAtUtc, string? activeSessionId) =>
        !string.IsNullOrWhiteSpace(activeSessionId) &&
        expiresAtUtc.HasValue &&
        expiresAtUtc.Value.ToUniversalTime() > UtcNow;
}
