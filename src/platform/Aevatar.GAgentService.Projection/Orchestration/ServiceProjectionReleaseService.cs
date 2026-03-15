using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionReleaseService<TContext>
    : ProjectionReleaseServiceBase<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>>
    where TContext : class, IProjectionContext
{
    public ServiceProjectionReleaseService(
        IProjectionLifecycleService<TContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override TContext ResolveContext(ServiceProjectionRuntimeLease<TContext> runtimeLease) =>
        runtimeLease.Context;
}
