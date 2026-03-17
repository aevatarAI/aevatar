namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Durable materialization port base with activation and optional release only.
/// </summary>
public abstract class MaterializationProjectionPortBase<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionMaterializationActivationService<TRuntimeLease> _activationService;
    private readonly IProjectionMaterializationReleaseService<TRuntimeLease>? _releaseService;

    protected MaterializationProjectionPortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionMaterializationActivationService<TRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<TRuntimeLease>? releaseService = null)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService;
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    protected async Task<TRuntimeLease?> EnsureProjectionAsync(
        ProjectionMaterializationStartRequest request,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || request == null || string.IsNullOrWhiteSpace(request.RootActorId))
            return null;

        return await _activationService.EnsureAsync(request, ct);
    }

    protected Task ReleaseProjectionAsync(TRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled || _releaseService == null)
            return Task.CompletedTask;

        return _releaseService.ReleaseIfIdleAsync(runtimeLease, ct);
    }
}
