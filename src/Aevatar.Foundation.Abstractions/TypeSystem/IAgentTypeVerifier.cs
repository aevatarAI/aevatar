namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Verifies whether an actor is of an expected agent type.
/// </summary>
public interface IAgentTypeVerifier
{
    /// <summary>
    /// Returns true when the actor can be proven to be assignable to <paramref name="expectedType"/>.
    /// </summary>
    Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default);
}
