using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRolloutProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceRolloutRuntimeLease, ServiceRolloutProjectionContext, IReadOnlyList<string>>
{
    public ServiceRolloutProjectionReleaseService(
        IProjectionLifecycleService<ServiceRolloutProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceRolloutProjectionContext ResolveContext(ServiceRolloutRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
