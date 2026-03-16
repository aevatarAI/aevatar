namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionReleaseService<TRuntimeLease, TContext, TTopology>
    : IProjectionPortReleaseService<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionContext
{
    private readonly IProjectionLifecycleService<TContext, TTopology> _lifecycle;

    public ContextProjectionReleaseService(IProjectionLifecycleService<TContext, TTopology> lifecycle)
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

        await _lifecycle.StopAsync(runtimeLease.Context, ct);
        if (runtimeLease is IProjectionRuntimeLeaseStopHandler stopHandler)
            await stopHandler.OnProjectionStoppedAsync(ct);
    }
}
