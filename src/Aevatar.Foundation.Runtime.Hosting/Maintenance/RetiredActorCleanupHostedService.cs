using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

/// <summary>
/// Spec-driven startup cleanup for actors whose persisted runtime types have been
/// retired. Each module contributes one or more <see cref="IRetiredActorSpec"/>
/// instances via DI; this service iterates them, probes each declared target,
/// destroys actors whose persisted type matches a retired token, removes upstream
/// relays, deletes module-owned read models, and resets the event stream.
///
/// Idempotent by design: a clean cluster sees no targets and does no destructive
/// work. New retired types take effect on the next pod startup with zero per-spec
/// completion gating — the spec list itself is the only source of truth.
///
/// Marker stream coordination prevents two pods running the same spec
/// simultaneously during a startup wave (lease + stale timeout). It does not gate
/// future runs; the cleanup runs every startup until the spec is removed.
/// </summary>
public sealed class RetiredActorCleanupHostedService : IHostedService
{
    private const int MarkerInProgress = 1;
    private const int MarkerReleased = 2;

    private readonly IEnumerable<IRetiredActorSpec> _specs;
    private readonly IActorTypeProbe _typeProbe;
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly IEventStore _eventStore;
    private readonly IEventStoreMaintenance _eventStoreMaintenance;
    private readonly IServiceProvider _services;
    private readonly RetiredActorCleanupOptions _options;
    private readonly ILogger<RetiredActorCleanupHostedService> _logger;

    public RetiredActorCleanupHostedService(
        IEnumerable<IRetiredActorSpec> specs,
        IActorTypeProbe typeProbe,
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider,
        IEventStore eventStore,
        IEventStoreMaintenance eventStoreMaintenance,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<RetiredActorCleanupHostedService> logger)
    {
        _specs = specs ?? throw new ArgumentNullException(nameof(specs));
        _typeProbe = typeProbe ?? throw new ArgumentNullException(nameof(typeProbe));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _eventStoreMaintenance = eventStoreMaintenance ?? throw new ArgumentNullException(nameof(eventStoreMaintenance));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = RetiredActorCleanupOptions.FromConfiguration(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Retired actor cleanup is disabled.");
            return;
        }

        try
        {
            foreach (var spec in _specs)
            {
                await RunSpecAsync(spec, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private async Task RunSpecAsync(IRetiredActorSpec spec, CancellationToken ct)
    {
        var lease = await TryAcquireLeaseAsync(spec.SpecId, ct).ConfigureAwait(false);
        if (lease == null)
            return;

        try
        {
            await foreach (var dynamicTarget in spec.DiscoverDynamicTargetsAsync(_services, ct).ConfigureAwait(false))
            {
                if (!await IsLeaseOwnerAsync(spec.SpecId, lease, ct).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Retired actor cleanup lease lost while processing spec {SpecId}. actorId={ActorId}",
                        spec.SpecId,
                        dynamicTarget.ActorId);
                    return;
                }

                await CleanupTargetAsync(spec, dynamicTarget, ct).ConfigureAwait(false);
            }

            foreach (var target in spec.Targets)
            {
                if (!await IsLeaseOwnerAsync(spec.SpecId, lease, ct).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Retired actor cleanup lease lost while processing spec {SpecId}. actorId={ActorId}",
                        spec.SpecId,
                        target.ActorId);
                    return;
                }

                await CleanupTargetAsync(spec, target, ct).ConfigureAwait(false);
            }

            await ReleaseLeaseAsync(spec.SpecId, lease, ct).ConfigureAwait(false);
            _logger.LogInformation("Retired actor cleanup completed for spec {SpecId}.", spec.SpecId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupTargetAsync(IRetiredActorSpec spec, RetiredActorTarget target, CancellationToken ct)
    {
        var runtimeTypeName = await _typeProbe
            .GetRuntimeAgentTypeNameAsync(target.ActorId, ct)
            .ConfigureAwait(false);
        var matchesRetiredRuntimeType = target.MatchesRuntimeType(runtimeTypeName);
        var shouldContinueReset = false;
        if (!matchesRetiredRuntimeType)
        {
            if (!string.IsNullOrWhiteSpace(runtimeTypeName))
                return;

            shouldContinueReset = target.ResetWhenRuntimeTypeUnavailable &&
                                  await HasEventStreamAsync(target.ActorId, ct).ConfigureAwait(false);
            if (!shouldContinueReset)
                return;
        }

        if (shouldContinueReset)
        {
            _logger.LogInformation(
                "Retired actor stream cleanup continuing after actor state was already cleared. specId={SpecId} actorId={ActorId}",
                spec.SpecId,
                target.ActorId);
        }

        if (!string.IsNullOrWhiteSpace(target.SourceStreamId))
        {
            await _streamProvider
                .GetStream(target.SourceStreamId)
                .RemoveRelayAsync(target.ActorId, ct)
                .ConfigureAwait(false);
        }

        await CleanupOutgoingRelaysBestEffortAsync(spec, target.ActorId, ct).ConfigureAwait(false);

        if (target.CleanupReadModels && _options.CleanupReadModels)
            await CleanupReadModelsBestEffortAsync(spec, target.ActorId, ct).ConfigureAwait(false);

        await _actorRuntime.DestroyAsync(target.ActorId, ct).ConfigureAwait(false);
        if (_options.ResetEventStreams)
            await _eventStoreMaintenance.ResetStreamAsync(target.ActorId, ct).ConfigureAwait(false);

        if (!matchesRetiredRuntimeType)
        {
            _logger.LogInformation(
                "Retired actor stream cleaned. specId={SpecId} actorId={ActorId}",
                spec.SpecId,
                target.ActorId);
            return;
        }

        _logger.LogInformation(
            "Retired actor cleaned. specId={SpecId} actorId={ActorId} runtimeType={RuntimeType}",
            spec.SpecId,
            target.ActorId,
            runtimeTypeName);
    }

    private async Task CleanupOutgoingRelaysBestEffortAsync(IRetiredActorSpec spec, string actorId, CancellationToken ct)
    {
        try
        {
            var stream = _streamProvider.GetStream(actorId);
            var relays = await stream.ListRelaysAsync(ct).ConfigureAwait(false);
            foreach (var relay in relays)
            {
                if (!string.IsNullOrWhiteSpace(relay.TargetStreamId))
                    await stream.RemoveRelayAsync(relay.TargetStreamId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Retired actor outgoing stream topology cleanup failed and will be skipped. specId={SpecId} actorId={ActorId}",
                spec.SpecId,
                actorId);
        }
    }

    private async Task CleanupReadModelsBestEffortAsync(IRetiredActorSpec spec, string actorId, CancellationToken ct)
    {
        try
        {
            await spec.DeleteReadModelsForActorAsync(_services, actorId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Retired actor read-model cleanup failed and will be skipped. specId={SpecId} actorId={ActorId}",
                spec.SpecId,
                actorId);
        }
    }

    private async Task<bool> HasEventStreamAsync(string actorId, CancellationToken ct) =>
        await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false) > 0;

    private async Task<CleanupLease?> TryAcquireLeaseAsync(string specId, CancellationToken ct)
    {
        var markerStreamId = MarkerStreamId(specId);
        var timeout = TimeSpan.FromSeconds(_options.InProgressTimeoutSeconds);
        var pollDelay = TimeSpan.FromMilliseconds(_options.WaitPollMilliseconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var marker = await ReadMarkerAsync(markerStreamId, ct).ConfigureAwait(false);
            if (marker.Latest is { Status: MarkerInProgress } latest && !IsStale(latest, timeout))
            {
                await Task.Delay(pollDelay, ct).ConfigureAwait(false);
                continue;
            }

            var lease = new CleanupLease(Guid.NewGuid().ToString("N"));
            try
            {
                await AppendMarkerAsync(
                    markerStreamId,
                    MarkerInProgress,
                    lease.Token,
                    marker.CurrentVersion,
                    ct).ConfigureAwait(false);
                return lease;
            }
            catch (EventStoreOptimisticConcurrencyException)
            {
                continue;
            }
        }
    }

    private async Task<bool> IsLeaseOwnerAsync(string specId, CleanupLease lease, CancellationToken ct)
    {
        var marker = await ReadMarkerAsync(MarkerStreamId(specId), ct).ConfigureAwait(false);
        return marker.Latest is { Status: MarkerInProgress } latest &&
               string.Equals(latest.Token, lease.Token, StringComparison.Ordinal);
    }

    private async Task ReleaseLeaseAsync(string specId, CleanupLease lease, CancellationToken ct)
    {
        var markerStreamId = MarkerStreamId(specId);
        var marker = await ReadMarkerAsync(markerStreamId, ct).ConfigureAwait(false);
        if (marker.Latest is not { Status: MarkerInProgress } latest ||
            !string.Equals(latest.Token, lease.Token, StringComparison.Ordinal))
        {
            _logger.LogWarning("Retired actor cleanup lease was lost before release for spec {SpecId}.", specId);
            return;
        }

        await AppendMarkerAsync(
            markerStreamId,
            MarkerReleased,
            lease.Token,
            marker.CurrentVersion,
            ct).ConfigureAwait(false);
    }

    private async Task<MarkerSnapshot> ReadMarkerAsync(string markerStreamId, CancellationToken ct)
    {
        var events = await _eventStore.GetEventsAsync(markerStreamId, ct: ct).ConfigureAwait(false);
        MarkerRecord? latest = null;
        foreach (var evt in events)
        {
            var parsed = TryParseMarker(evt);
            if (parsed != null)
                latest = parsed;
        }

        var version = events.Count > 0 ? events[^1].Version : 0;
        return new MarkerSnapshot(version, latest);
    }

    private async Task AppendMarkerAsync(
        string markerStreamId,
        int status,
        string token,
        long expectedVersion,
        CancellationToken ct)
    {
        await _eventStore.AppendAsync(
            markerStreamId,
            [
                new StateEvent
                {
                    AgentId = markerStreamId,
                    EventId = token,
                    EventType = Int32Value.Descriptor.FullName,
                    EventData = Any.Pack(new Int32Value { Value = status }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = expectedVersion + 1,
                },
            ],
            expectedVersion,
            ct).ConfigureAwait(false);
    }

    private static MarkerRecord? TryParseMarker(StateEvent evt)
    {
        if (evt.EventData == null || !evt.EventData.TryUnpack<Int32Value>(out var status))
            return null;

        return new MarkerRecord(
            status.Value,
            evt.EventId ?? string.Empty,
            evt.Timestamp?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            evt.Version);
    }

    private static bool IsStale(MarkerRecord marker, TimeSpan timeout) =>
        DateTimeOffset.UtcNow - marker.Timestamp > timeout;

    private static string MarkerStreamId(string specId) =>
        $"__maintenance:retired-actor-cleanup:{specId}";

    private sealed record CleanupLease(string Token);

    private sealed record MarkerRecord(
        int Status,
        string Token,
        DateTimeOffset Timestamp,
        long Version);

    private sealed record MarkerSnapshot(long CurrentVersion, MarkerRecord? Latest);
}
