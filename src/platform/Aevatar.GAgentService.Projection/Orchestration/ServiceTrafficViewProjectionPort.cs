using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceTrafficViewProjectionPort
    : ServiceProjectionPortBase<ServiceTrafficViewProjectionContext>,
      IServiceTrafficViewProjectionPort
{
    public ServiceTrafficViewProjectionPort(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>> activationService)
        : base(activationService, ServiceProjectionNames.Traffic)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
