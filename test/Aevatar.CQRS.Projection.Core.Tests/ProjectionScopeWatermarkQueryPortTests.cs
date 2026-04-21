using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeWatermarkQueryPortTests
{
    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReplaysProjectionScopeWatermark()
    {
        var eventStore = new InMemoryEventStore();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "root-actor",
            "channel-bot-registration",
            ProjectionRuntimeMode.DurableMaterialization);
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);

        await eventStore.AppendAsync(
            scopeActorId,
            [
                CreateStateEvent(
                    1,
                    new ProjectionScopeStartedEvent
                    {
                        RootActorId = scopeKey.RootActorId,
                        ProjectionKind = scopeKey.ProjectionKind,
                        Mode = ProjectionScopeMode.DurableMaterialization,
                    }),
                CreateStateEvent(
                    2,
                    new ProjectionScopeWatermarkAdvancedEvent
                    {
                        LastObservedVersion = 7,
                        LastSuccessfulVersion = 7,
                    }),
                CreateStateEvent(
                    3,
                    new ProjectionScopeWatermarkAdvancedEvent
                    {
                        LastObservedVersion = 11,
                        LastSuccessfulVersion = 11,
                    }),
            ],
            expectedVersion: 0);

        var sut = new EventStoreProjectionScopeWatermarkQueryPort(eventStore);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().Be(11);
    }

    [Fact]
    public async Task GetLastSuccessfulVersionAsync_ReturnsNullWhenScopeWasReleased()
    {
        var eventStore = new InMemoryEventStore();
        var scopeKey = new ProjectionRuntimeScopeKey(
            "root-actor",
            "agent-registry",
            ProjectionRuntimeMode.DurableMaterialization);
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);

        await eventStore.AppendAsync(
            scopeActorId,
            [
                CreateStateEvent(
                    1,
                    new ProjectionScopeStartedEvent
                    {
                        RootActorId = scopeKey.RootActorId,
                        ProjectionKind = scopeKey.ProjectionKind,
                        Mode = ProjectionScopeMode.DurableMaterialization,
                    }),
                CreateStateEvent(2, new ProjectionScopeReleasedEvent()),
            ],
            expectedVersion: 0);

        var sut = new EventStoreProjectionScopeWatermarkQueryPort(eventStore);

        var watermark = await sut.GetLastSuccessfulVersionAsync(scopeKey);

        watermark.Should().BeNull();
    }

    private static StateEvent CreateStateEvent(long version, IMessage payload) =>
        new()
        {
            AgentId = "projection-scope",
            EventId = $"evt-{version}",
            Version = version,
            Timestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)),
            EventType = payload.Descriptor.FullName,
            EventData = Any.Pack(payload),
        };

    private sealed class InMemoryEventStore : IEventStore
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
                throw new InvalidOperationException();

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream[^1].Version,
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
}
