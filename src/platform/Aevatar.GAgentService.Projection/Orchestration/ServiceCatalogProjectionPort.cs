using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceCatalogProjectionPort
    : ServiceProjectionPortBase<ServiceCatalogProjectionContext>,
      IServiceCatalogProjectionPort
{
    public ServiceCatalogProjectionPort(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>> activationService)
        : base(activationService, ServiceProjectionNames.Catalog)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
