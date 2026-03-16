namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionMaterializationReleaseService<TRuntimeLease, TContext>
    : IProjectionMaterializationReleaseService<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly IProjectionMaterializationLifecycleService<TContext, TRuntimeLease> _lifecycle;

    public ContextProjectionMaterializationReleaseService(
        IProjectionMaterializationLifecycleService<TContext, TRuntimeLease> lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public Task ReleaseIfIdleAsync(TRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ct.ThrowIfCancellationRequested();
        return _lifecycle.StopAsync(runtimeLease, ct);
    }
}
