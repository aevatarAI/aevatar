namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Reads runtime agent kind information for an actor.
/// </summary>
public interface IActorKindProbe
{
    /// <summary>
    /// Gets the runtime agent kind for the actor, or null when unavailable.
    /// </summary>
    Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default);
}
