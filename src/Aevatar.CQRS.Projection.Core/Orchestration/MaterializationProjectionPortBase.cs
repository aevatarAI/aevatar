namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Durable materialization port base for existing read-model leases and optional release only.
/// </summary>
public abstract class MaterializationProjectionPortBase<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionScopeReleaseService<TRuntimeLease>? _releaseService;

    protected MaterializationProjectionPortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionScopeReleaseService<TRuntimeLease>? releaseService = null)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _releaseService = releaseService;
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    protected Task ReleaseProjectionAsync(TRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        // Refactor (iter101/cluster-104): Old projection port base exposed protected EnsureProjectionAsync backed by activation service; new request/query-facing ports attach to existing readmodels only, while committed-state/startup/background binders own activation.
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled || _releaseService == null)
            return Task.CompletedTask;

        return _releaseService.ReleaseIfIdleAsync(runtimeLease, ct);
    }
}
