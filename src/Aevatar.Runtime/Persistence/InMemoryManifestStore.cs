// ─────────────────────────────────────────────────────────────
// InMemoryManifestStore - in-memory manifest storage.
// Stores AgentManifest by agent ID and supports listing for restore-all flows.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Persistence;

namespace Aevatar;

/// <summary>In-memory implementation of agent manifest storage.</summary>
public sealed class InMemoryManifestStore : IAgentManifestStore
{
    private readonly ConcurrentDictionary<string, AgentManifest> _store = new();

    /// <summary>Loads manifest for the specified agent.</summary>
    public Task<AgentManifest?> LoadAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(agentId));

    /// <summary>Saves manifest for the specified agent.</summary>
    public Task SaveAsync(string agentId, AgentManifest manifest, CancellationToken ct = default)
    { _store[agentId] = manifest; return Task.CompletedTask; }

    /// <summary>Deletes manifest for the specified agent.</summary>
    public Task DeleteAsync(string agentId, CancellationToken ct = default)
    { _store.TryRemove(agentId, out _); return Task.CompletedTask; }

    /// <summary>Lists all manifests, used by RestoreAll.</summary>
    public Task<IReadOnlyList<AgentManifest>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentManifest>>(_store.Values.ToList());
}
