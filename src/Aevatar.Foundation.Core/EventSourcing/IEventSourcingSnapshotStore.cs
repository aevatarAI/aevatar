namespace Aevatar.Foundation.Core.EventSourcing;

public interface IEventSourcingSnapshotStore<TState>
    where TState : class
{
    Task<EventSourcingSnapshot<TState>?> LoadAsync(
        string agentId,
        CancellationToken ct = default);

    Task SaveAsync(
        string agentId,
        EventSourcingSnapshot<TState> snapshot,
        CancellationToken ct = default);
}
