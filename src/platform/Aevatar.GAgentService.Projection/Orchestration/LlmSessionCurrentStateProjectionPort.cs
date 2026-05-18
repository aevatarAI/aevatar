using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class LlmSessionCurrentStateProjectionPort
    : ServiceProjectionPortBase<LlmSessionCurrentStateProjectionContext>,
      ILlmSessionCurrentStateProjectionPort
{
    public LlmSessionCurrentStateProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<LlmSessionCurrentStateProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<LlmSessionCurrentStateProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.ResponseSessions)
    {
    }

    public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, ct);
}
