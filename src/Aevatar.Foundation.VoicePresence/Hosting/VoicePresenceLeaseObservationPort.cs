using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoicePresenceLeaseObservationPort : IVoicePresenceLeaseObservationPort
{
    private static readonly TimeSpan DefaultObservationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(100);

    private readonly IVoicePresenceCapabilityQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _observationTimeout;
    private readonly TimeSpan _observationInterval;
    private readonly ILogger<VoicePresenceLeaseObservationPort>? _logger;

    public VoicePresenceLeaseObservationPort(
        IVoicePresenceCapabilityQueryPort queryPort,
        ILogger<VoicePresenceLeaseObservationPort>? logger = null)
        : this(queryPort, null, DefaultObservationTimeout, ObservationInterval, logger)
    {
    }

    internal VoicePresenceLeaseObservationPort(
        IVoicePresenceCapabilityQueryPort queryPort,
        TimeProvider? timeProvider,
        TimeSpan observationTimeout,
        TimeSpan observationInterval,
        ILogger<VoicePresenceLeaseObservationPort>? logger = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observationTimeout = observationTimeout;
        _observationInterval = observationInterval;
        _logger = logger;
    }

    public Task<VoicePresenceCapabilitySnapshot> ObserveSessionLeaseAsync(
        VoicePresenceSessionLeaseRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ObserveAsync(
            request.ActorId,
            request.ModuleName,
            snapshot => MatchesSessionLease(request, snapshot, _timeProvider.GetUtcNow()),
            "Voice presence session lease was not observed.",
            ct);
    }

    public Task<VoicePresenceCapabilitySnapshot> ObserveTransportAttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        string transportLeaseId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportLeaseId);
        return ObserveAsync(
            handle.ActorId,
            handle.ModuleName,
            snapshot => MatchesTransportAttach(handle, transportLeaseId, snapshot, _timeProvider.GetUtcNow()),
            "Voice presence transport attach was not observed.",
            ct);
    }

    private async Task<VoicePresenceCapabilitySnapshot> ObserveAsync(
        string actorId,
        string moduleName,
        Func<VoicePresenceCapabilitySnapshot, bool> matches,
        string failureMessage,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_observationTimeout);

        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                var snapshot = await _queryPort.GetAsync(actorId, moduleName, timeoutCts.Token);
                if (snapshot != null && matches(snapshot))
                    return snapshot;

                await Task.Delay(_observationInterval, _timeProvider, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The grain accepted the lease/attach, but the read-model projection never reflected it
            // within the budget. A sustained spike of this warning means the capability projection
            // write path is broken (e.g. a stuck Garnet OCC / version gap), distinct from transient lag.
            _logger?.LogWarning(
                "voice lease observation timed out after {TimeoutSeconds}s for actor {ActorId}/{ModuleName}: {FailureMessage} — capability projection did not reflect the grain state (lag or stuck read model)",
                _observationTimeout.TotalSeconds,
                actorId,
                moduleName,
                failureMessage);
            throw new TimeoutException(failureMessage);
        }
    }

    private static bool MatchesSessionLease(
        VoicePresenceSessionLeaseRequest request,
        VoicePresenceCapabilitySnapshot snapshot,
        DateTimeOffset utcNow) =>
        MatchesActorModule(request.ActorId, request.ModuleName, snapshot) &&
        string.Equals(snapshot.ActiveSessionId, request.SessionId, StringComparison.Ordinal) &&
        string.Equals(snapshot.ActiveLeaseOwnerId, request.OwnerId, StringComparison.Ordinal) &&
        snapshot.StateVersion > request.ObservedStateVersion &&
        snapshot.LeaseEpoch > 0 &&
        HasActiveLeaseExpiry(snapshot.LeaseExpiresAt, utcNow);

    private static bool MatchesTransportAttach(
        VoicePresenceSessionLeaseHandle handle,
        string transportLeaseId,
        VoicePresenceCapabilitySnapshot snapshot,
        DateTimeOffset utcNow) =>
        MatchesActorModule(handle.ActorId, handle.ModuleName, snapshot) &&
        string.Equals(snapshot.ActiveSessionId, handle.SessionId, StringComparison.Ordinal) &&
        string.Equals(snapshot.ActiveLeaseOwnerId, handle.OwnerId, StringComparison.Ordinal) &&
        snapshot.TransportAttached &&
        string.Equals(snapshot.ActiveTransportLeaseId, transportLeaseId, StringComparison.Ordinal) &&
        snapshot.LeaseEpoch == handle.LeaseEpoch &&
        snapshot.LeaseEpoch > 0 &&
        HasActiveLeaseExpiry(snapshot.LeaseExpiresAt, utcNow);

    private static bool MatchesActorModule(
        string actorId,
        string moduleName,
        VoicePresenceCapabilitySnapshot snapshot) =>
        string.Equals(snapshot.ActorId, actorId, StringComparison.Ordinal) &&
        string.Equals(snapshot.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase);

    private static bool HasActiveLeaseExpiry(DateTimeOffset? leaseExpiresAt, DateTimeOffset utcNow) =>
        leaseExpiresAt.HasValue &&
        leaseExpiresAt.Value.ToUniversalTime() > utcNow;
}
