using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class GAgentRunTerminalProjectionPort
    : ServiceProjectionPortBase<GAgentRunTerminalProjectionContext>,
      IGAgentRunTerminalProjectionPort
{
    public GAgentRunTerminalProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeActivationService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> activationService,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>> releaseService)
        : base(options, activationService, releaseService, ServiceProjectionKinds.GAgentRunTerminal)
    {
    }

    public Task EnsureProjectionAsync(
        string actorId,
        string correlationId,
        CancellationToken ct = default) =>
        EnsureProjectionCoreAsync(actorId, correlationId, ct);

    private async Task EnsureProjectionCoreAsync(
        string actorId,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(correlationId))
            return;

        _ = await EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ServiceProjectionKinds.GAgentRunTerminal,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
                SessionId = correlationId.Trim(),
            },
            ct);
    }
}
