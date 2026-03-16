using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceServingSetProjectionPort
    : ServiceProjectionPortBase<ServiceServingSetProjectionContext>,
      IServiceServingSetProjectionPort
{
    public ServiceServingSetProjectionPort(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>> activationService)
        : base(activationService, ServiceProjectionNames.Serving)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
