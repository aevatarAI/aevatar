// ─────────────────────────────────────────────────────────────
// InMemoryStateStore - in-memory state storage.
// Stores and loads generic state by agent ID, suitable for development/testing.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Abstractions.Persistence;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>In-memory implementation of agent state storage.</summary>
/// <typeparam name="TState">State type, must be a class.</typeparam>
public sealed class InMemoryStateStore<TState> : IStateStore<TState> where TState : class
{
    private readonly ConcurrentDictionary<string, TState> _store = new();

    /// <summary>Loads state for the specified agent.</summary>
    public Task<TState?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        AgentMetrics.StateLoads.Add(1, [new("state.type", typeof(TState).Name)]);
        return Task.FromResult(_store.GetValueOrDefault(agentId));
    }

    /// <summary>Saves state for the specified agent.</summary>
    public Task SaveAsync(string agentId, TState state, CancellationToken ct = default)
    {
        _store[agentId] = state;
        AgentMetrics.StateSaves.Add(1, [new("state.type", typeof(TState).Name)]);
        return Task.CompletedTask;
    }

    /// <summary>Deletes state for the specified agent.</summary>
    public Task DeleteAsync(string agentId, CancellationToken ct = default)
    { _store.TryRemove(agentId, out _); return Task.CompletedTask; }
}
