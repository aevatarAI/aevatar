using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRevisionCatalogProjectionReleaseService
    : ProjectionReleaseServiceBase<ServiceRevisionCatalogRuntimeLease, ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>
{
    public ServiceRevisionCatalogProjectionReleaseService(
        IProjectionLifecycleService<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServiceRevisionCatalogProjectionContext ResolveContext(ServiceRevisionCatalogRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
