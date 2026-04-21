using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogProjectionPort
    : MaterializationProjectionPortBase<UserAgentCatalogMaterializationRuntimeLease>
{
    public UserAgentCatalogProjectionPort(
        IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<UserAgentCatalogMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
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
