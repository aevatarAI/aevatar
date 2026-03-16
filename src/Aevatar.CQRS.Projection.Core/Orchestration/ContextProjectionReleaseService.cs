namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionReleaseService<TRuntimeLease, TContext>
    : IProjectionSessionReleaseService<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionSessionContext
{
    private readonly IProjectionLifecycleService<TContext, TRuntimeLease> _lifecycle;

    public ContextProjectionReleaseService(IProjectionLifecycleService<TContext, TRuntimeLease> lifecycle)
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

        await _lifecycle.StopAsync(runtimeLease, ct);
        if (runtimeLease is IProjectionRuntimeLeaseStopHandler stopHandler)
            await stopHandler.OnProjectionStoppedAsync(ct);
    }
}
