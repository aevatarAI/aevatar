namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Template base for projection release services:
/// check idle -> stop lifecycle -> post-stop(optional).
/// </summary>
public abstract class ProjectionReleaseServiceBase<TRuntimeLease, TContext, TTopology>
    : IProjectionPortReleaseService<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease
    where TContext : class, IProjectionContext
{
    private readonly IProjectionLifecycleService<TContext, TTopology> _lifecycle;

    protected ProjectionReleaseServiceBase(IProjectionLifecycleService<TContext, TTopology> lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task ReleaseIfIdleAsync(
        TRuntimeLease runtimeLease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();

        if (runtimeLease.GetLiveSinkSubscriptionCount() > 0)
            return;

        var context = ResolveContext(runtimeLease);
        await _lifecycle.StopAsync(context, ct);
        if (runtimeLease is IProjectionRuntimeLeaseStopHandler stopHandler)
            await stopHandler.OnProjectionStoppedAsync(ct);
        await OnStoppedAsync(runtimeLease, context, ct);
    }

    protected abstract TContext ResolveContext(TRuntimeLease runtimeLease);

    protected virtual Task OnStoppedAsync(
        TRuntimeLease runtimeLease,
        TContext context,
        CancellationToken ct) => Task.CompletedTask;
}
