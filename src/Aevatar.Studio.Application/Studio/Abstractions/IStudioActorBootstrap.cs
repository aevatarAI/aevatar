using Aevatar.Foundation.Abstractions;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Single entry point that Studio actor-backed stores use to bring an actor
/// online. Keeps command paths focused on actor provisioning; committed-state
/// projection activation is owned by the projection activation plan provider.
///
/// Callers are constrained to actor types that statically declare their
/// projection kind via <see cref="IProjectedActor"/>, which closes the
/// previous loose-binding where any caller could pass an arbitrary kind
/// string that didn't match the agent type.
///
/// The projection marker remains a compile-time binding between actor type
/// and readmodel kind for projection-owned activation plans.
/// </summary>
public interface IStudioActorBootstrap
{
    /// <summary>
    /// Gets the existing actor with the given ID or creates a new one of
    /// type <typeparamref name="TAgent"/>.
    /// </summary>
    Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor;
}
