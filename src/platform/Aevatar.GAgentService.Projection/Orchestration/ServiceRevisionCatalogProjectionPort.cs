using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRevisionCatalogProjectionPort
    : ServiceProjectionPortBase<ServiceRevisionCatalogProjectionContext>,
      IServiceRevisionCatalogProjectionPort
{
    public ServiceRevisionCatalogProjectionPort(
        IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>> activationService)
        : base(activationService, ServiceProjectionNames.Revisions)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
