using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceServingSetProjectionPort
    : ServiceProjectionPortBase<ServiceServingSetProjectionContext>,
      IServiceServingSetProjectionPort
{
    public ServiceServingSetProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.Serving)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
