// ─────────────────────────────────────────────────────────────
// IActor - actor wrapper contract.
// Pure RPC surface: no in-process IAgent reference exposed.
// Works identically for Local and Orleans runtimes.
// ─────────────────────────────────────────────────────────────

namespace Aevatar;

/// <summary>
/// Actor wrapper contract. All methods are RPC-safe — they work
/// identically whether the agent runs in-process (Local) or in
/// a remote Grain (Orleans).
/// </summary>
public interface IActor
{
    /// <summary>Unique actor identifier.</summary>
    string Id { get; }

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

    /// <summary>Gets a human-readable agent description.</summary>
    Task<string> GetDescriptionAsync();

    /// <summary>Gets the agent type name (short name, e.g. "RoleGAgent").</summary>
    Task<string> GetAgentTypeNameAsync();

    /// <summary>
    /// Sends a JSON configuration to the agent. The agent interprets it
    /// based on its <c>TConfig</c> type (see GAgentBase&lt;TState,TConfig&gt;).
    /// </summary>
    /// <param name="configJson">JSON-serialized config object.</param>
    Task ConfigureAsync(string configJson, CancellationToken ct = default);
}
