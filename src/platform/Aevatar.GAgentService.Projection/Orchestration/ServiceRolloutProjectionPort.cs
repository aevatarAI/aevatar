using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRolloutProjectionPort
    : ServiceProjectionPortBase<ServiceRolloutProjectionContext>,
      IServiceRolloutProjectionPort
{
    public ServiceRolloutProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Rollouts)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
