using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.Executors;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Reconciles one probe-target actor configuration command per manifest entry
/// at host startup and once per minute so rolling deployments reactivate actors
/// whose process-local tick was lost. Once active, each actor self-reschedules
/// its probe tick from inside its own event loop. The service also releases legacy
/// nested status scopes left by older hosts, but does not own normal projection
/// activation, the ongoing schedule, or long-lived projection state.
///
/// Failures here only affect the affected target's first activation; the host
/// continues to start so unrelated services are not blocked by a single bad
/// manifest entry.
/// </summary>
public sealed class HealthProbeStartupService : IHostedService, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);

    private readonly StatusDashboardManifest _manifest;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IHealthProbeExecutorRegistry _executorRegistry;
    private readonly IProjectionScopeAttachExistingLeaseLookup<ProjectionScopeStatusRuntimeLease>? _statusScopeLookup;
    private readonly IProjectionScopeReleaseService<ProjectionScopeStatusRuntimeLease>? _statusScopeReleaseService;
    private readonly ILogger<HealthProbeStartupService> _logger;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _stopping;
    private Task? _reconcileLoop;

    public HealthProbeStartupService(
        IOptions<StatusDashboardOptions> options,
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        IHealthProbeExecutorRegistry executorRegistry,
        ILogger<HealthProbeStartupService> logger,
        IProjectionScopeAttachExistingLeaseLookup<ProjectionScopeStatusRuntimeLease>? statusScopeLookup = null,
        IProjectionScopeReleaseService<ProjectionScopeStatusRuntimeLease>? statusScopeReleaseService = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _manifest = StatusDashboardManifest.FromOptions(options.Value ?? new StatusDashboardOptions());
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _executorRegistry = executorRegistry ?? throw new ArgumentNullException(nameof(executorRegistry));
        _statusScopeLookup = statusScopeLookup;
        _statusScopeReleaseService = statusScopeReleaseService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var currentSlugs = _manifest.Descriptors
            .Select(static descriptor => descriptor.Slug)
            .Where(static slug => !RetiredStatusProbeTargets.Contains(slug))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var slug in _manifest.Descriptors
                     .Select(static descriptor => descriptor.Slug)
                     .Concat(RetiredStatusProbeTargets.Slugs)
                     .Where(static slug => !string.IsNullOrWhiteSpace(slug))
                     .Distinct(StringComparer.Ordinal))
        {
            await ReleaseLegacyProjectionScopesIfExistsAsync(slug, ct);
        }

        if (_manifest.Descriptors.Count == 0)
        {
            _logger.LogInformation("Status dashboard manifest is empty — no probes to schedule.");
        }
        else
        {
            foreach (var descriptor in _manifest.Descriptors)
            {
                if (RetiredStatusProbeTargets.Contains(descriptor.Slug))
                    continue;

                if (_executorRegistry.Resolve(descriptor.ProbeKind) == null)
                {
                    _logger.LogError(
                        "Status probe {Slug} declares unknown probe_kind '{Kind}'. Known: [{Known}]. Skipping.",
                        descriptor.Slug, descriptor.ProbeKind, string.Join(",", _executorRegistry.KnownKinds));
                    continue;
                }

                await EnsureProbeAsync(descriptor, ct);
            }
        }

        foreach (var retiredSlug in RetiredStatusProbeTargets.Slugs)
        {
            if (currentSlugs.Contains(retiredSlug))
                continue;

            await RetireProbeIfExistsAsync(retiredSlug, ct);
        }

        if (!ct.IsCancellationRequested && _manifest.Descriptors.Any(descriptor =>
                !RetiredStatusProbeTargets.Contains(descriptor.Slug) &&
                _executorRegistry.Resolve(descriptor.ProbeKind) != null))
        {
            _stopping = new CancellationTokenSource();
            _reconcileLoop = RunPeriodicReconcileAsync(_stopping.Token);
        }
    }

    private static HealthProbeTargetDescriptor RetiredProbeDescriptor(string slug) =>
        new()
        {
            Slug = slug,
            DisplayName = slug,
            Category = "feature",
            ProbeKind = "http_status",
            IntervalSeconds = 300,
            TimeoutMs = 5_000,
            Enabled = false,
        };

    private async Task ReleaseLegacyProjectionScopesIfExistsAsync(string slug, CancellationToken ct)
    {
        var healthScopeKey = new ProjectionRuntimeScopeKey(
            HealthProbeStoreCommands.BuildActorId(slug),
            HealthProbeTargetGAgent.LegacyProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization);
        var healthScopeActorId = ProjectionScopeActorId.Build(healthScopeKey);

        await ReleaseLegacyHealthScopeIfExistsAsync(healthScopeKey, healthScopeActorId, slug, ct);

        if (_statusScopeLookup == null || _statusScopeReleaseService == null)
            return;

        var request = new ProjectionScopeStartRequest
        {
            RootActorId = healthScopeActorId,
            ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        };

        try
        {
            var lease = await _statusScopeLookup.TryGetAsync(request, ct);
            if (lease == null)
                return;

            await _statusScopeReleaseService.ReleaseIfIdleAsync(lease, ct);
            _logger.LogInformation(
                "Dispatched release for legacy health projection status scope {Slug}.",
                slug);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release legacy health projection status scope for {Slug}; startup will continue.",
                slug);
        }
    }

    private async Task ReleaseLegacyHealthScopeIfExistsAsync(
        ProjectionRuntimeScopeKey scopeKey,
        string scopeActorId,
        string slug,
        CancellationToken ct)
    {
        try
        {
            if (!await _actorRuntime.ExistsAsync(scopeActorId))
                return;

            var command = new ReleaseProjectionScopeCommand
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
                SessionId = scopeKey.SessionId,
                Mode = ProjectionScopeMode.DurableMaterialization,
            };
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(command),
                Route = EnvelopeRouteSemantics.CreateDirect("status.health-probe-cleanup", scopeActorId),
            };
            _ = await _dispatchPort.DispatchAsync(scopeActorId, envelope, ct);
            _logger.LogInformation("Dispatched release for legacy health projection scope {Slug}.", slug);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release legacy health projection scope for {Slug}; startup will continue.",
                slug);
        }
    }

    private async Task RetireProbeIfExistsAsync(string slug, CancellationToken ct)
    {
        var actorId = HealthProbeStoreCommands.BuildActorId(slug);
        var actor = await _actorRuntime.GetAsync(actorId);
        if (actor == null)
            return;

        await EnsureProbeAsync(RetiredProbeDescriptor(slug), ct);
    }

    private async Task EnsureProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
    {
        // Refactor (iter47/cluster-005-status-dashboard-startup-projection-activation):
        //   Old pattern: Startup service explicitly ensures projection scopes and uses Task.Delay retry before dispatching configure commands.
        //   New principle: Startup path dispatches actor configuration only; projection activation owned by committed-state hooks; retry uses hosted-service scheduling.
        try
        {
            await HealthProbeStoreCommands.DispatchConfigureAsync(
                _actorRuntime, _dispatchPort, descriptor, ct);
            _logger.LogInformation(
                "Status probe {Slug} configuration dispatched (probe={Kind}, interval={Interval}s)",
                descriptor.Slug, descriptor.ProbeKind, descriptor.IntervalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to dispatch status probe {Slug} configuration; it will appear with unknown status on /status until configuration is dispatched again",
                descriptor.Slug);
        }
    }

    private async Task RunPeriodicReconcileAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ReconcileInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var descriptor in _manifest.Descriptors)
                {
                    if (!RetiredStatusProbeTargets.Contains(descriptor.Slug) &&
                        _executorRegistry.Resolve(descriptor.ProbeKind) != null)
                    {
                        await EnsureProbeAsync(descriptor, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_stopping == null || _reconcileLoop == null)
            return;

        await _stopping.CancelAsync();
        try
        {
            await _reconcileLoop.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _stopping?.Cancel();
        _stopping?.Dispose();
    }
}
