using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ResponsesAgentToolStateCurrentStateProjectionPort
    : ServiceProjectionPortBase<ResponsesAgentToolStateCurrentStateProjectionContext>,
      IResponsesAgentToolStateCurrentStateProjectionPort
{
    public ResponsesAgentToolStateCurrentStateProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<ResponsesAgentToolStateCurrentStateProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<ResponsesAgentToolStateCurrentStateProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.ResponsesAgentTools)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
