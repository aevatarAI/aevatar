// ─────────────────────────────────────────────────────────────
// InMemoryEventStore - in-memory event store.
// Supports append operations with optimistic concurrency for event sourcing.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>In-memory event store with append and version-based query support.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private sealed class EventStreamState
    {
        public long CurrentVersion { get; set; }

        public List<StateEvent> Events { get; } = [];
    }

    private readonly ConcurrentDictionary<string, EventStreamState> _store = new();
    private readonly object _lock = new();

    /// <summary>Appends events with optimistic concurrency check on expectedVersion.</summary>
    /// <param name="agentId">Agent ID.</param>
    /// <param name="events">Events to append.</param>
    /// <param name="expectedVersion">Expected current version; throws when mismatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Latest version after append.</returns>
    public Task<EventStoreCommitResult> AppendAsync(string agentId, IEnumerable<StateEvent> events, long expectedVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var stream = _store.GetOrAdd(agentId, _ => new EventStreamState());
            var current = stream.CurrentVersion;
            if (current != expectedVersion)
                throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {current}");
            var eventList = events.ToList();
            stream.Events.AddRange(eventList.Select(static x => x.Clone()));
            if (eventList.Count > 0)
                stream.CurrentVersion = eventList[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.CurrentVersion,
                CommittedEvents = { eventList.Select(static x => x.Clone()) },
            });
        }
    }

    /// <summary>Gets events for an agent, optionally filtered by fromVersion.</summary>
    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(string agentId, long? fromVersion = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_store.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Events.Where(e => e.Version > fromVersion.Value).Select(static x => x.Clone()).ToList()
                : stream.Events.Select(static x => x.Clone()).ToList();
            return Task.FromResult(result);
        }
    }

    /// <summary>Gets the current version for the specified agent.</summary>
    public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(!_store.TryGetValue(agentId, out var stream) ? 0L : stream.CurrentVersion);
        }
    }
}
