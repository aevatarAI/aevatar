using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Activates the projection materialization scope for a per-(platform,
/// tenant, external_user_id) <see cref="ExternalIdentityBindingGAgent"/>.
/// MUST be called before <see cref="ExternalIdentityBindingProjectionQueryPort"/>
/// /<see cref="ExternalIdentityBindingProjectionReadinessPort"/> can return
/// the binding for that actor — without an active scope, the projector
/// never subscribes to the actor's committed event stream and the
/// readmodel stays empty.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Aevatar.GAgents.Channel.Identity.AevatarOAuthClientProjectionPort"/>.
/// Pre-this-port, the binding scope was never activated for any actor and
/// every legacy cluster's binding readmodel was empty even when the
/// actor's State held an active binding — the OAuth callback's readiness
/// wait would time out, and the next inbound message's binding gate would
/// keep sending the user back to /init forever (issue #549 follow-up
/// observed 2026-05-01: <c>CommitBinding discarded: already bound</c>
/// without a corresponding readmodel materialization).
/// </remarks>
public sealed class ExternalIdentityBindingProjectionPort
    : MaterializationProjectionPortBase<ExternalIdentityBindingMaterializationRuntimeLease>
{
    public const string ProjectionKind = "external-identity-binding";

    public ExternalIdentityBindingProjectionPort(
        IProjectionScopeActivationService<ExternalIdentityBindingMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<ExternalIdentityBindingMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
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
