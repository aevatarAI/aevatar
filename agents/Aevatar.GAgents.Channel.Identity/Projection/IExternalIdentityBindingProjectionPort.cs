using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Abstraction for activating the projection materialization scope for a per-(platform,
/// tenant, external_user_id) <see cref="ExternalIdentityBindingGAgent"/>. Consumers
/// (OAuth endpoints, identity slash-command self-heal) must depend on this interface
/// per CLAUDE.md "依赖反转" rather than the concrete
/// <see cref="ExternalIdentityBindingProjectionPort"/> — that gives the host a seam to
/// swap implementations (e.g. fire-and-forget self-heal in tests vs. a real activation
/// service in production).
/// </summary>
public interface IExternalIdentityBindingProjectionPort
{
    Task<ExternalIdentityBindingMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default);
}
