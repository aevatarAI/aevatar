using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRolloutProjectionPort
    : ServiceProjectionPortBase<ServiceRolloutProjectionContext>,
      IServiceRolloutProjectionPort
{
    public ServiceRolloutProjectionPort(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>> activationService)
        : base(activationService, ServiceProjectionNames.Rollouts)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
