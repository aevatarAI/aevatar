using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Uses RuntimeActorGrain persistent state as snapshot storage for event sourcing.
/// </summary>
internal sealed class RuntimeActorGrainEventSourcingSnapshotStore<TState>
    : IEventSourcingSnapshotStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private static readonly string StateTypeName = typeof(TState).FullName ?? typeof(TState).Name;

    private readonly IPersistentState<RuntimeActorGrainState> _runtimeState;

    public RuntimeActorGrainEventSourcingSnapshotStore(IPersistentState<RuntimeActorGrainState> runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public RuntimeActorGrainEventSourcingSnapshotStore(IRuntimeActorStateBindingAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _runtimeState = accessor.Current
            ?? throw new InvalidOperationException(
                "Runtime actor state is not bound. " +
                "Resolve IEventSourcingSnapshotStore<TState> only within RuntimeActorGrain binding context.");
    }

    public Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var snapshot = _runtimeState.State.AgentStateSnapshot;
        if (snapshot == null || snapshot.Length == 0)
            return Task.FromResult<EventSourcingSnapshot<TState>?>(null);

        if (!string.Equals(_runtimeState.State.AgentStateTypeName, StateTypeName, StringComparison.Ordinal))
            return Task.FromResult<EventSourcingSnapshot<TState>?>(null);

        var state = new TState();
        state.MergeFrom(snapshot);
        return Task.FromResult<EventSourcingSnapshot<TState>?>(
            new EventSourcingSnapshot<TState>(state, _runtimeState.State.AgentStateSnapshotVersion));
    }

    public Task SaveAsync(string agentId, EventSourcingSnapshot<TState> snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        _runtimeState.State.AgentStateTypeName = StateTypeName;
        _runtimeState.State.AgentStateSnapshot = snapshot.State.ToByteArray();
        _runtimeState.State.AgentStateSnapshotVersion = snapshot.Version;
        return _runtimeState.WriteStateAsync();
    }
}
