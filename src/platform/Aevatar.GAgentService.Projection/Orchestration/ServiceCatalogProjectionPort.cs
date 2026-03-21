using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceCatalogProjectionPort
    : ServiceProjectionPortBase<ServiceCatalogProjectionContext>,
      IServiceCatalogProjectionPort
{
    public ServiceCatalogProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Catalog)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
