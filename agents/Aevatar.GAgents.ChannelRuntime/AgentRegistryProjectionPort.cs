using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class AgentRegistryProjectionPort
    : MaterializationProjectionPortBase<AgentRegistryMaterializationRuntimeLease>
{
    public AgentRegistryProjectionPort(
        IProjectionScopeActivationService<AgentRegistryMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<AgentRegistryMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = "agent-registry",
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
