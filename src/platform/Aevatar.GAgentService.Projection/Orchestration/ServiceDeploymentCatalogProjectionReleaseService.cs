using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceDeploymentCatalogProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceDeploymentCatalogRuntimeLease, ServiceDeploymentCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceDeploymentCatalogProjectionReleaseService(
        IProjectionLifecycleService<ServiceDeploymentCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceDeploymentCatalogProjectionContext ResolveContext(ServiceDeploymentCatalogRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
