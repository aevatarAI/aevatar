using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceTrafficViewProjectionPort
    : ServiceProjectionPortBase<ServiceTrafficViewProjectionContext>,
      IServiceTrafficViewProjectionPort
{
    public ServiceTrafficViewProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Traffic)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
