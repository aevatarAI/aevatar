using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ResponseSessionCurrentStateProjectionPort
    : ServiceProjectionPortBase<ResponseSessionCurrentStateProjectionContext>,
      IResponseSessionCurrentStateProjectionPort
{
    public ResponseSessionCurrentStateProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ResponseSessionCurrentStateProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ResponseSessionCurrentStateProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.ResponseSessions)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
