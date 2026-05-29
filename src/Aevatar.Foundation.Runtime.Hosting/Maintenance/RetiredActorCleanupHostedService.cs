using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// Actor-owned coordinator leases prevent two pods running the same spec
/// simultaneously during a startup wave (lease + stale timeout). They do not
/// gate future runs; the cleanup runs every startup until the spec is removed.
/// </summary>
public sealed class RetiredActorCleanupHostedService : IHostedService
{
    // Stable cleanupReason values for log/metric distinguishing of normal-match
    // vs orphaned-stream recovery paths. Ops dashboards group on these strings.
    private const string CleanupReasonRetiredTypeMatch = "retired-type-match";
    private const string CleanupReasonOrphanedEventStream = "orphaned-event-stream";

    private readonly IEnumerable<IRetiredActorSpec> _specs;
    private readonly IActorTypeProbe _typeProbe;
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly IEventStore _eventStore;
    private readonly IEventStoreMaintenance _eventStoreMaintenance;
    private readonly IRetiredActorCleanupCoordinatorPort _coordinator;
    private readonly IStreamPubSubMaintenance? _streamPubSubMaintenance;
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
        IRetiredActorCleanupCoordinatorPort coordinator,
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
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        // Pub/sub maintenance is optional — backends without persistent
        // rendezvous state (in-memory streams) don't register an implementation.
        _streamPubSubMaintenance = services.GetService<IStreamPubSubMaintenance>();
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
        catch (Exception ex)
        {
            await _coordinator.RecordFailureAsync(lease, ex, ct).ConfigureAwait(false);
            throw;
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

        var cleanupReason = matchesRetiredRuntimeType
            ? CleanupReasonRetiredTypeMatch
            : CleanupReasonOrphanedEventStream;

        if (shouldContinueReset)
        {
            _logger.LogInformation(
                "Retired actor stream cleanup recovering orphaned stream after partial cleanup. specId={SpecId} actorId={ActorId} cleanupReason={CleanupReason}",
                spec.SpecId,
                target.ActorId,
                cleanupReason);
        }

        if (!string.IsNullOrWhiteSpace(target.SourceStreamId))
            await CleanupIncomingRelayBestEffortAsync(spec, target, ct).ConfigureAwait(false);

        await CleanupOutgoingRelaysBestEffortAsync(spec, target.ActorId, ct).ConfigureAwait(false);

        if (target.CleanupReadModels && _options.CleanupReadModels)
            await CleanupReadModelsBestEffortAsync(spec, target.ActorId, ct).ConfigureAwait(false);

        await _actorRuntime.DestroyAsync(target.ActorId, ct).ConfigureAwait(false);
        if (_options.ResetEventStreams)
            await _eventStoreMaintenance.ResetStreamAsync(target.ActorId, ct).ConfigureAwait(false);

        // Reset stream pub/sub rendezvous state AFTER the actor + event stream
        // are gone so the next silo wave's stream-producer registration does
        // not collide with stale etag from the previous incarnation.
        await CleanupStreamPubSubBestEffortAsync(spec, target.ActorId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Retired actor cleaned. specId={SpecId} actorId={ActorId} runtimeType={RuntimeType} cleanupReason={CleanupReason}",
            spec.SpecId,
            target.ActorId,
            runtimeTypeName ?? string.Empty,
            cleanupReason);
    }

    private async Task CleanupIncomingRelayBestEffortAsync(
        IRetiredActorSpec spec, RetiredActorTarget target, CancellationToken ct)
    {
        try
        {
            await _streamProvider
                .GetStream(target.SourceStreamId!)
                .RemoveRelayAsync(target.ActorId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Retired actor incoming stream relay removal failed and will be skipped. specId={SpecId} actorId={ActorId} sourceStreamId={SourceStreamId}",
                spec.SpecId,
                target.ActorId,
                target.SourceStreamId);
        }
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

    private async Task CleanupStreamPubSubBestEffortAsync(IRetiredActorSpec spec, string actorId, CancellationToken ct)
    {
        if (_streamPubSubMaintenance == null)
            return;

        try
        {
            await _streamPubSubMaintenance.ResetActorStreamPubSubAsync(actorId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Retired actor stream pub/sub state reset failed and will be skipped. specId={SpecId} actorId={ActorId}",
                spec.SpecId,
                actorId);
        }
    }

    private async Task<bool> HasEventStreamAsync(string actorId, CancellationToken ct) =>
        await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false) > 0;

    // Refactor (issue1056/r3-consensus): Old pattern: this hosted service owned
    // marker stream replay/append lease state. New principle: hosted cleanup is
    // only the executor; per-spec ownership is checked through the coordinator.
    private Task<RetiredActorCleanupLeaseHandle?> TryAcquireLeaseAsync(string specId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return _coordinator.TryAcquireAsync(
            specId,
            Environment.MachineName,
            now,
            now.AddSeconds(_options.InProgressTimeoutSeconds),
            ct);
    }

    private Task<bool> IsLeaseOwnerAsync(
        string specId,
        RetiredActorCleanupLeaseHandle lease,
        CancellationToken ct)
    {
        _ = specId;
        return _coordinator.CheckAsync(lease, ct);
    }

    private async Task ReleaseLeaseAsync(
        string specId,
        RetiredActorCleanupLeaseHandle lease,
        CancellationToken ct)
    {
        _ = specId;
        if (!await _coordinator.CheckAsync(lease, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Retired actor cleanup lease was lost before release for spec {SpecId}.", lease.SpecId);
            return;
        }

        await _coordinator.ReleaseAsync(lease, ct).ConfigureAwait(false);
    }
}
