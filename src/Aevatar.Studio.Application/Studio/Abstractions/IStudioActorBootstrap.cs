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

    /// <summary>
    /// Gets an already-initialized Studio actor and activates its projection
    /// scope. Returns <c>null</c> when the actor has not been created. Command
    /// paths that operate on existing identities use this to avoid creating an
    /// empty actor merely because a route supplied an id.
    /// </summary>
    Task<IActor?> GetExistingAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor;

    /// <summary>
    /// Gets an already-initialized Studio actor without touching projection
    /// lifecycle. Durable projection continuations use this when they need to
    /// report completion back to the same actor whose committed event is
    /// currently being observed; re-ensuring that same projection scope from
    /// inside its actor turn would create a self-wait in local runtimes.
    /// </summary>
    Task<IActor?> GetExistingActorAsync<TAgent>(string actorId, CancellationToken ct = default)
        where TAgent : IAgent, IProjectedActor;
}
