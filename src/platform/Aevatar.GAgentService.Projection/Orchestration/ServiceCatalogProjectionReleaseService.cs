using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceCatalogProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceCatalogRuntimeLease, ServiceCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceCatalogProjectionReleaseService(
        IProjectionLifecycleService<ServiceCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceCatalogProjectionContext ResolveContext(ServiceCatalogRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
