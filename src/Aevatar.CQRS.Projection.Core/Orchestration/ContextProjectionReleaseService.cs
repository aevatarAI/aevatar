namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionReleaseService<TRuntimeLease, TContext, TTopology>
    : ProjectionReleaseServiceBase<TRuntimeLease, TContext, TTopology>
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionContext
{
    public ContextProjectionReleaseService(IProjectionLifecycleService<TContext, TTopology> lifecycle)
        : base(lifecycle)
    {
    }

    protected override TContext ResolveContext(TRuntimeLease runtimeLease) => runtimeLease.Context;
}
