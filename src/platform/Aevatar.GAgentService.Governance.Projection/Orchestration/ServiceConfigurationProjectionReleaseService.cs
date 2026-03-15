using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceConfigurationProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceConfigurationRuntimeLease, ServiceConfigurationProjectionContext, IReadOnlyList<string>>
{
    public ServiceConfigurationProjectionReleaseService(
        IProjectionLifecycleService<ServiceConfigurationProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceConfigurationProjectionContext ResolveContext(ServiceConfigurationRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
