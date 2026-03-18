using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceDeploymentCatalogProjectionPort
    : ServiceProjectionPortBase<ServiceDeploymentCatalogProjectionContext>,
      IServiceDeploymentCatalogProjectionPort
{
    public ServiceDeploymentCatalogProjectionPort(
        ServiceProjectionOptions options,
        IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>> activationService,
        IProjectionMaterializationReleaseService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Deployments)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
