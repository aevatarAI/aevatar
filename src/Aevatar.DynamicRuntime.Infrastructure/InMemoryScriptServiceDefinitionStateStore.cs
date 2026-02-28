using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryScriptServiceDefinitionStateStore : IStateStore<ScriptServiceDefinitionState>
{
    private readonly ConcurrentDictionary<string, ScriptServiceDefinitionState> _states = new(StringComparer.Ordinal);

    public Task<ScriptServiceDefinitionState?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states.TryGetValue(agentId, out var state);
        return Task.FromResult(state?.Clone());
    }

    public Task SaveAsync(string agentId, ScriptServiceDefinitionState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states[agentId] = state.Clone();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _states.TryRemove(agentId, out _);
        return Task.CompletedTask;
    }
}
