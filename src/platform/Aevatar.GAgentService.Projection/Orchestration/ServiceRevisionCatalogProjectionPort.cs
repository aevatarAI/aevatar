using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRevisionCatalogProjectionPort
    : ServiceProjectionPortBase<ServiceRevisionCatalogProjectionContext>,
      IServiceRevisionCatalogProjectionPort
{
    public ServiceRevisionCatalogProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Revisions)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
