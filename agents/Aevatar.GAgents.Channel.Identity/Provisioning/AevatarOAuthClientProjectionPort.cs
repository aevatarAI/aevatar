using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Activates the projection materialization scope for the cluster-singleton
/// <see cref="AevatarOAuthClientGAgent"/>. MUST be called before any reader
/// hits <see cref="AevatarOAuthClientProjectionProvider"/> — without an
/// active scope, the projector never subscribes to the actor's committed
/// event stream and the readmodel stays empty (so /init keeps reporting
/// "still bootstrapping" forever even after DCR succeeds).
/// </summary>
public sealed class AevatarOAuthClientProjectionPort
    : MaterializationProjectionPortBase<AevatarOAuthClientMaterializationRuntimeLease>
{
    public const string ProjectionKind = "aevatar-oauth-client";

    public AevatarOAuthClientProjectionPort(
        IProjectionScopeActivationService<AevatarOAuthClientMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<AevatarOAuthClientMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
