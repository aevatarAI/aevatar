using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServiceEndpointCatalogProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceEndpointCatalogRuntimeLease, ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceEndpointCatalogProjectionReleaseService(
        IProjectionLifecycleService<ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceEndpointCatalogProjectionContext ResolveContext(ServiceEndpointCatalogRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
