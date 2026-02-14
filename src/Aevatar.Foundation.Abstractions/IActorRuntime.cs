// ─────────────────────────────────────────────────────────────
// IActorRuntime - actor runtime contract.
// Manages actor lifecycle, topology, lookup, and restoration.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Actor runtime contract for lifecycle and topology management.
/// </summary>
public interface IActorRuntime
{
    /// <summary>Creates and registers an actor for the specified agent type.</summary>
    /// <typeparam name="TAgent">Agent type.</typeparam>
    /// <param name="id">Optional ID. Auto-generated when null.</param>
    Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent;

    /// <summary>Creates and registers an actor for the specified agent type.</summary>
    /// <param name="agentType">Agent type.</param>
    /// <param name="id">Optional ID. Auto-generated when null.</param>
    Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default);

    /// <summary>Destroys the actor with the specified ID.</summary>
    Task DestroyAsync(string id, CancellationToken ct = default);

    /// <summary>Gets an actor by ID, or null if it does not exist.</summary>
    Task<IActor?> GetAsync(string id);

    /// <summary>Gets all actors in the current runtime.</summary>
    Task<IReadOnlyList<IActor>> GetAllAsync();

    /// <summary>Checks whether an actor with the specified ID exists.</summary>
    Task<bool> ExistsAsync(string id);

    /// <summary>Creates a parent-child link by attaching childId under parentId.</summary>
    Task LinkAsync(string parentId, string childId, CancellationToken ct = default);

    /// <summary>Removes the parent-child link for childId.</summary>
    Task UnlinkAsync(string childId, CancellationToken ct = default);

    /// <summary>Restores previously created agents from persistence (for process restart).</summary>
    Task RestoreAllAsync(CancellationToken ct = default);
}
