using System.Collections.Concurrent;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>
/// In-memory snapshot store for event sourcing state snapshots.
/// </summary>
public sealed class InMemoryEventSourcingSnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly ConcurrentDictionary<string, EventSourcingSnapshot<TState>> _snapshots = new(StringComparer.Ordinal);

    public Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        if (!_snapshots.TryGetValue(agentId, out var snapshot))
            return Task.FromResult<EventSourcingSnapshot<TState>?>(null);

        return Task.FromResult<EventSourcingSnapshot<TState>?>(
            new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version));
    }

    public Task SaveAsync(string agentId, EventSourcingSnapshot<TState> snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        _snapshots[agentId] = new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version);
        return Task.CompletedTask;
    }
}
