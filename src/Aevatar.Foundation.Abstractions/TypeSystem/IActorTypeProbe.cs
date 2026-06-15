namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Reads runtime agent type information for an actor.
/// </summary>
public interface IActorTypeProbe
{
    /// <summary>
    /// Gets runtime agent type name for the actor, or null when unavailable.
    /// </summary>
    Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default);
}
