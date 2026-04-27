using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceRunCurrentStateProjectionPort
    : ServiceProjectionPortBase<ServiceRunCurrentStateProjectionContext>,
      IServiceRunCurrentStateProjectionPort
{
    public ServiceRunCurrentStateProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceRunCurrentStateProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceRunCurrentStateProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Runs)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
