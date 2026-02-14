// ─────────────────────────────────────────────────────────────
// IAgentManifestStore - agent manifest persistence contract.
// Stores framework-level agent metadata separately from business state.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Framework-level agent metadata persisted separately from business state.
/// </summary>
public sealed class AgentManifest
{
    /// <summary>Unique agent identifier.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>Assembly-qualified agent type name.</summary>
    public string AgentTypeName { get; set; } = "";

    /// <summary>Associated module names.</summary>
    public List<string> ModuleNames { get; set; } = [];

    /// <summary>Optional JSON configuration payload.</summary>
    public string? ConfigJson { get; set; }

    /// <summary>Extensible metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Agent manifest storage contract for framework-level metadata.
/// </summary>
public interface IAgentManifestStore
{
    /// <summary>Loads manifest for the specified agent, or null when absent.</summary>
    Task<AgentManifest?> LoadAsync(string agentId, CancellationToken ct = default);

    /// <summary>Saves manifest for the specified agent.</summary>
    Task SaveAsync(string agentId, AgentManifest manifest, CancellationToken ct = default);

    /// <summary>Deletes manifest for the specified agent.</summary>
    Task DeleteAsync(string agentId, CancellationToken ct = default);

    /// <summary>Lists manifests for all agents.</summary>
    Task<IReadOnlyList<AgentManifest>> ListAsync(CancellationToken ct = default);
}
