namespace Aevatar.Foundation.Abstractions.TypeSystem;

/// <summary>
/// Verifies whether an actor is bound to an expected primary agent kind.
/// </summary>
public interface IAgentKindVerifier
{
    /// <summary>
    /// Returns true when the actor can be proven to have <paramref name="expectedKind"/>.
    /// </summary>
    Task<bool> IsExpectedKindAsync(string actorId, string expectedKind, CancellationToken ct = default);
}
