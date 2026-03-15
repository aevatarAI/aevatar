using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceTrafficViewProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceTrafficViewRuntimeLease, ServiceTrafficViewProjectionContext, IReadOnlyList<string>>
{
    public ServiceTrafficViewProjectionReleaseService(
        IProjectionLifecycleService<ServiceTrafficViewProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceTrafficViewProjectionContext ResolveContext(ServiceTrafficViewRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
