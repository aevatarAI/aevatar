using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceBindingProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceBindingRuntimeLease, ServiceBindingProjectionContext, IReadOnlyList<string>>
{
    public ServiceBindingProjectionReleaseService(
        IProjectionLifecycleService<ServiceBindingProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceBindingProjectionContext ResolveContext(ServiceBindingRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
