using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;

namespace Aevatar.AI.Tests;

internal sealed class InMemoryEventStoreForTests : IEventStore
{
    private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

    public Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_events.TryGetValue(agentId, out var stream))
        {
            stream = [];
            _events[agentId] = stream;
        }

        var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
        if (currentVersion != expectedVersion)
            throw new InvalidOperationException($"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

        var appended = events.Select(x => x.Clone()).ToList();
        stream.AddRange(appended);
        var latest = stream.Count == 0 ? 0 : stream[^1].Version;
        return Task.FromResult(new EventStoreCommitResult
        {
            AgentId = agentId,
            LatestVersion = latest,
            CommittedEvents = { appended.Select(x => x.Clone()) },
        });
    }

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_events.TryGetValue(agentId, out var stream))
            return Task.FromResult<IReadOnlyList<StateEvent>>([]);

        IReadOnlyList<StateEvent> result = fromVersion.HasValue
            ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
            : stream.Select(x => x.Clone()).ToList();
        return Task.FromResult(result);
    }

    public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
            return Task.FromResult(0L);
        return Task.FromResult(stream[^1].Version);
    }

    public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
            return Task.FromResult(0L);

        var before = stream.Count;
        stream.RemoveAll(x => x.Version <= toVersion);
        return Task.FromResult((long)(before - stream.Count));
    }
}
