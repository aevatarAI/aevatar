// ─────────────────────────────────────────────────────────────
// IEventStore — Event sourcing storage
// Optional capability: Agents use via ES mixin
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Event sourcing storage. Append state events, query by version, get current version.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Append state events. expectedVersion is used for optimistic concurrency control.
    /// Returns the committed event records and latest version after appending.
    /// </summary>
    Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default);

    /// <summary>Query events by version range.</summary>
    Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default);

    /// <summary>Get current latest version number.</summary>
    Task<long> GetVersionAsync(string agentId, CancellationToken ct = default);
}
