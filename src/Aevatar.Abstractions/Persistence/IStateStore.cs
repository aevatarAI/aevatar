// ─────────────────────────────────────────────────────────────
// IStateStore - simple state persistence contract.
// Supports loading, saving, and deleting agent state.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Persistence;

/// <summary>
/// Agent state storage contract: Load / Save / Delete.
/// </summary>
public interface IStateStore<TState> where TState : class
{
    /// <summary>Loads agent state, or null when no state exists.</summary>
    Task<TState?> LoadAsync(string agentId, CancellationToken ct = default);

    /// <summary>Saves agent state.</summary>
    Task SaveAsync(string agentId, TState state, CancellationToken ct = default);

    /// <summary>Deletes agent state.</summary>
    Task DeleteAsync(string agentId, CancellationToken ct = default);
}
