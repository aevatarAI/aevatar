using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceServingSetProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceServingSetRuntimeLease, ServiceServingSetProjectionContext, IReadOnlyList<string>>
{
    public ServiceServingSetProjectionReleaseService(
        IProjectionLifecycleService<ServiceServingSetProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceServingSetProjectionContext ResolveContext(ServiceServingSetRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
