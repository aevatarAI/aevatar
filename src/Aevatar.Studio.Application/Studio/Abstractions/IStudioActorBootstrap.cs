using Aevatar.Foundation.Abstractions;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Single entry point that Studio actor-backed stores use to bring an actor
/// online. Combines "ensure the actor exists" with "activate the Studio
/// projection scope for this actor" so the two concerns don't leak into
/// every business store.
///
/// Callers are constrained to actor types that statically declare their
/// projection kind via <see cref="IProjectedActor"/>, which closes the
/// previous loose-binding where any caller could pass an arbitrary kind
/// string that didn't match the agent type.
///
/// Mirrors the spirit of the governance and channel-runtime projection
/// ports while hiding the two-step dance behind a single compile-time-
/// checked call.
/// </summary>
public interface IStudioActorBootstrap
{
    /// <summary>
    /// Gets the existing actor with the given ID or creates a new one of
    /// type <typeparamref name="TAgent"/>, then ensures the Studio
    /// materialization scope is active for this actor so committed events
    /// are materialized into the read-model document store.
    /// </summary>
    Task<IActor> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor;
}
