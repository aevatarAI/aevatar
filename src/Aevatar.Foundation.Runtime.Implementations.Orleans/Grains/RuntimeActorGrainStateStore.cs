using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Uses RuntimeActorGrain's persistent state as the backing store for agent state snapshots.
/// </summary>
internal sealed class RuntimeActorGrainStateStore<TState>
    : IStateStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private static readonly string StateTypeName = typeof(TState).FullName ?? typeof(TState).Name;

    private readonly IPersistentState<RuntimeActorGrainState> _runtimeState;

    public RuntimeActorGrainStateStore(IPersistentState<RuntimeActorGrainState> runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public RuntimeActorGrainStateStore(IRuntimeActorStateBindingAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _runtimeState = accessor.Current
                        ?? throw new InvalidOperationException(
                            "Runtime actor state is not bound. " +
                            "Resolve IStateStore<TState> only within RuntimeActorGrain binding context.");
    }

    public Task<TState?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var snapshot = _runtimeState.State.AgentStateSnapshot;
        if (snapshot == null || snapshot.Length == 0)
            return Task.FromResult<TState?>(null);

        // Snapshot belongs to another state type (e.g. actor type migrated).
        if (!string.Equals(_runtimeState.State.AgentStateTypeName, StateTypeName, StringComparison.Ordinal))
            return Task.FromResult<TState?>(null);

        var state = new TState();
        state.MergeFrom(snapshot);
        return Task.FromResult<TState?>(state);
    }

    public Task SaveAsync(string agentId, TState state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(state);
        ct.ThrowIfCancellationRequested();

        _runtimeState.State.AgentStateTypeName = StateTypeName;
        _runtimeState.State.AgentStateSnapshot = state.ToByteArray();
        return _runtimeState.WriteStateAsync();
    }

    public Task DeleteAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        _runtimeState.State.AgentStateTypeName = null;
        _runtimeState.State.AgentStateSnapshot = null;
        return _runtimeState.WriteStateAsync();
    }
}
