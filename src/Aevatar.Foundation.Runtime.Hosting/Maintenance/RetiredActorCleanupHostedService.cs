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
/// Startup only triggers the cleanup pass. Each target is revalidated immediately
/// before destructive work, so duplicate startup waves converge through no-op
/// checks instead of EventStore marker coordination.
/// </summary>
// Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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
    private readonly IStreamPubSubMaintenance? _streamPubSubMaintenance;
    private readonly IServiceProvider _services;
    private readonly RetiredActorCleanupOptions _options;
    private readonly ILogger<RetiredActorCleanupHostedService> _logger;
    private CancellationTokenSource? _cleanupCts;
    private Task? _cleanupTask;

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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
        // Pub/sub maintenance is optional — backends without persistent
        // rendezvous state (in-memory streams) don't register an implementation.
        _streamPubSubMaintenance = services.GetService<IStreamPubSubMaintenance>();
        _options = RetiredActorCleanupOptions.FromConfiguration(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    public Task Completion => _cleanupTask ?? Task.CompletedTask;

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Retired actor cleanup is disabled.");
            return Task.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        _cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cleanupTask = Task.Run(() => RunCleanupPassAsync(_cleanupCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _cleanupCts?.Cancel();
        return Task.CompletedTask;
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private async Task RunCleanupPassAsync(CancellationToken ct)
    {
        try
        {
            foreach (var spec in _specs)
            {
                await RunSpecAsync(spec, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retired actor cleanup pass failed.");
        }
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private async Task RunSpecAsync(IRetiredActorSpec spec, CancellationToken ct)
    {
        try
        {
            await foreach (var dynamicTarget in spec.DiscoverDynamicTargetsAsync(_services, ct).ConfigureAwait(false))
            {
                await CleanupTargetAsync(spec, dynamicTarget, ct).ConfigureAwait(false);
            }

            foreach (var target in spec.Targets)
            {
                await CleanupTargetAsync(spec, target, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Retired actor cleanup pass finished for spec {SpecId}.", spec.SpecId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private async Task CleanupTargetAsync(IRetiredActorSpec spec, RetiredActorTarget target, CancellationToken ct)
    {
        var candidate = await RevalidateTargetForCleanupAsync(target, ct).ConfigureAwait(false);
        if (!candidate.ShouldClean)
            return;

        var revalidation = await RevalidateTargetForCleanupAsync(target, ct).ConfigureAwait(false);
        if (!revalidation.ShouldClean)
            return;

        if (revalidation.ShouldContinueReset)
        {
            _logger.LogInformation(
                "Retired actor stream cleanup recovering orphaned stream after partial cleanup. specId={SpecId} actorId={ActorId} cleanupReason={CleanupReason}",
                spec.SpecId,
                target.ActorId,
                revalidation.CleanupReason);
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
            revalidation.RuntimeTypeName ?? string.Empty,
            revalidation.CleanupReason);
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private async Task<CleanupTargetRevalidation> RevalidateTargetForCleanupAsync(
        RetiredActorTarget target,
        CancellationToken ct)
    {
        var runtimeTypeName = await _typeProbe
            .GetRuntimeAgentTypeNameAsync(target.ActorId, ct)
            .ConfigureAwait(false);
        if (target.MatchesRuntimeType(runtimeTypeName))
        {
            return new CleanupTargetRevalidation(
                ShouldClean: true,
                RuntimeTypeName: runtimeTypeName,
                ShouldContinueReset: false,
                CleanupReason: CleanupReasonRetiredTypeMatch);
        }

        if (!string.IsNullOrWhiteSpace(runtimeTypeName))
        {
            return new CleanupTargetRevalidation(
                ShouldClean: false,
                RuntimeTypeName: runtimeTypeName,
                ShouldContinueReset: false,
                CleanupReason: string.Empty);
        }

        var shouldContinueReset = target.ResetWhenRuntimeTypeUnavailable &&
                                  await HasEventStreamAsync(target.ActorId, ct).ConfigureAwait(false);
        return new CleanupTargetRevalidation(
            ShouldClean: shouldContinueReset,
            RuntimeTypeName: runtimeTypeName,
            ShouldContinueReset: shouldContinueReset,
            CleanupReason: shouldContinueReset ? CleanupReasonOrphanedEventStream : string.Empty);
    }

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
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

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private async Task<bool> HasEventStreamAsync(string actorId, CancellationToken ct) =>
        await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false) > 0;

    // Refactor (issue1287-first): Old pattern: EventStore marker lease.  New principle: per-target revalidation fence + idempotent cleanup.
    private sealed record CleanupTargetRevalidation(
        bool ShouldClean,
        string? RuntimeTypeName,
        bool ShouldContinueReset,
        string CleanupReason);
}
