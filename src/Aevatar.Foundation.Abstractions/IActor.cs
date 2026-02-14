// ─────────────────────────────────────────────────────────────
// IActor - actor wrapper contract.
// Wraps IAgent and provides hierarchy, activation, and event dispatch capabilities.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Actor wrapper contract around an agent.
/// </summary>
public interface IActor
{
    /// <summary>Unique actor identifier.</summary>
    string Id { get; }

    /// <summary>Embedded agent instance.</summary>
    IAgent Agent { get; }

    /// <summary>Activates the actor and its agent.</summary>
    Task ActivateAsync(CancellationToken ct = default);

    /// <summary>Deactivates the actor and its agent.</summary>
    Task DeactivateAsync(CancellationToken ct = default);

    /// <summary>Dispatches an event envelope to the agent.</summary>
    Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default);

    /// <summary>Gets parent actor ID, or null when no parent exists.</summary>
    Task<string?> GetParentIdAsync();

    /// <summary>Gets child actor IDs.</summary>
    Task<IReadOnlyList<string>> GetChildrenIdsAsync();
}
