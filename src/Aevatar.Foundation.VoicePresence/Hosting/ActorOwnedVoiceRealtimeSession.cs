using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class ActorOwnedVoiceRealtimeSession
    : IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>
{
    internal static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TakeoverReadInterval = TimeSpan.FromMilliseconds(200);
    private const int TakeoverReadMaxAttempts = 10;
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

            // Attach with an existing/stale transport: TAKE IT OVER instead of rejecting with 409.
            // The lease release is SessionId-only (no expiry/owner/epoch gate), so this evicts even a
            // TTL-expired or relay-dead lease that left TransportAttached stuck — a new Start must never
            // be permanently blocked by a prior session's leftover state. AcquireAsync below bumps the
            // lease epoch, so the new session strictly out-fences the old relay's late frames.
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
            }

            // Bounded re-read until the evict is observed cleared, absorbing projection lag / a transient
            // write-skip so a single attach doesn't bounce. Fail closed only if still attached after the
            // budget (the next attach re-fences by epoch and succeeds).
            capability = await AwaitClearedCapabilityAsync(inbound.ActorId, inbound.ModuleName, ct);
            if (capability == null)
                return Failure(VoiceRealtimeSessionStartError.NotFound);
            if (!capability.Initialized)
                return Failure(VoiceRealtimeSessionStartError.NotInitialized);
            if (capability.TransportAttached || IsActive(capability.LeaseExpiresAt, capability.ActiveSessionId))
                return Failure(VoiceRealtimeSessionStartError.TransportAlreadyAttached);
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

    // After a takeover evict, poll the capability projection (bounded) until it observes the cleared
    // state, so a single attach absorbs projection lag instead of fail-closing on a stale read. Returns
    // the latest snapshot (the caller fail-closes if it is still attached after the budget).
    private async Task<VoicePresenceCapabilitySnapshot?> AwaitClearedCapabilityAsync(
        string actorId,
        string? moduleName,
        CancellationToken ct)
    {
        VoicePresenceCapabilitySnapshot? snapshot = null;
        for (var attempt = 0; attempt < TakeoverReadMaxAttempts; attempt++)
        {
            snapshot = await _capabilityQueryPort.GetAsync(actorId, moduleName, ct);
            if (snapshot == null)
                return null;
            if (!snapshot.TransportAttached && !IsActive(snapshot.LeaseExpiresAt, snapshot.ActiveSessionId))
                return snapshot;
            if (attempt < TakeoverReadMaxAttempts - 1)
                await Task.Delay(TakeoverReadInterval, _timeProvider, ct);
        }

        return snapshot;
    }
}
