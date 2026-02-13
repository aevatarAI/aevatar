// ─────────────────────────────────────────────────────────────
// InMemoryEventStore - in-memory event store.
// Supports append operations with optimistic concurrency for event sourcing.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Persistence;

namespace Aevatar;

/// <summary>In-memory event store with append and version-based query support.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<StateEvent>> _store = new();
    private readonly object _lock = new();

    /// <summary>Appends events with optimistic concurrency check on expectedVersion.</summary>
    /// <param name="agentId">Agent ID.</param>
    /// <param name="events">Events to append.</param>
    /// <param name="expectedVersion">Expected current version; throws when mismatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Latest version after append.</returns>
    public Task<long> AppendAsync(string agentId, IEnumerable<StateEvent> events, long expectedVersion, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _store.GetOrAdd(agentId, _ => []);
            var current = list.Count > 0 ? list[^1].Version : 0;
            if (current != expectedVersion)
                throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {current}");
            var eventList = events.ToList();
            list.AddRange(eventList);
            return Task.FromResult(eventList.Count > 0 ? eventList[^1].Version : current);
        }
    }

    /// <summary>Gets events for an agent, optionally filtered by fromVersion.</summary>
    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string agentId, long? fromVersion = null, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(agentId, out var list))
            return Task.FromResult<IReadOnlyList<StateEvent>>([]);
        IReadOnlyList<StateEvent> result = fromVersion.HasValue
            ? list.Where(e => e.Version > fromVersion.Value).ToList()
            : list.ToList();
        return Task.FromResult(result);
    }

    /// <summary>Gets the current version for the specified agent.</summary>
    public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(!_store.TryGetValue(agentId, out var list) || list.Count == 0 ? 0L : list[^1].Version);
}
