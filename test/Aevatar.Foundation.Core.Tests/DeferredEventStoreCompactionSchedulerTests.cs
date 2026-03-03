using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class DeferredEventStoreCompactionSchedulerTests
{
    [Fact]
    public async Task ScheduleAsync_ShouldOnlyRecordIntent_AndDeleteOnIdle()
    {
        var store = new InMemoryEventStore();
        await AppendEventsAsync(store, "agent-1", 3);
        var scheduler = new DeferredEventStoreCompactionScheduler(store);

        await scheduler.ScheduleAsync("agent-1", 2);

        (await store.GetEventsAsync("agent-1")).Count.ShouldBe(3);

        await scheduler.RunOnIdleAsync("agent-1");

        var remaining = await store.GetEventsAsync("agent-1");
        remaining.Count.ShouldBe(1);
        remaining[0].Version.ShouldBe(3);
        (await store.GetVersionAsync("agent-1")).ShouldBe(3);
    }

    [Fact]
    public async Task ScheduleAsync_ShouldCoalesceToMaxTargetVersion()
    {
        var store = new InMemoryEventStore();
        await AppendEventsAsync(store, "agent-2", 5);
        var scheduler = new DeferredEventStoreCompactionScheduler(store);

        await scheduler.ScheduleAsync("agent-2", 2);
        await scheduler.ScheduleAsync("agent-2", 4);
        await scheduler.ScheduleAsync("agent-2", 3);
        await scheduler.RunOnIdleAsync("agent-2");

        var remaining = await store.GetEventsAsync("agent-2");
        remaining.Count.ShouldBe(1);
        remaining[0].Version.ShouldBe(5);
    }

    [Fact]
    public async Task RunOnIdleAsync_WhenDeleteFails_ShouldRequeueForNextIdle()
    {
        var innerStore = new InMemoryEventStore();
        await AppendEventsAsync(innerStore, "agent-3", 3);
        var flakyStore = new FlakyDeleteEventStore(innerStore);
        var scheduler = new DeferredEventStoreCompactionScheduler(flakyStore);

        await scheduler.ScheduleAsync("agent-3", 2);
        await scheduler.RunOnIdleAsync("agent-3");

        (await innerStore.GetEventsAsync("agent-3")).Count.ShouldBe(3);

        await scheduler.RunOnIdleAsync("agent-3");

        var remaining = await innerStore.GetEventsAsync("agent-3");
        remaining.Count.ShouldBe(1);
        remaining[0].Version.ShouldBe(3);
        flakyStore.DeleteCalls.ShouldBe(2);
    }

    private static async Task AppendEventsAsync(IEventStore store, string agentId, int count)
    {
        var events = Enumerable.Range(1, count).Select(i => new StateEvent
        {
            EventId = $"{agentId}-e-{i}",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Version = i,
            EventType = typeof(Empty).FullName ?? nameof(Empty),
            EventData = Any.Pack(new Empty()),
            AgentId = agentId,
        });

        await store.AppendAsync(agentId, events, expectedVersion: 0);
    }

    private sealed class FlakyDeleteEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner;
        private int _deleteCalls;

        public FlakyDeleteEventStore(InMemoryEventStore inner)
        {
            _inner = inner;
        }

        public int DeleteCalls => _deleteCalls;

        public Task<long> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
            => _inner.AppendAsync(agentId, events, expectedVersion, ct);

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
            => _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
            => _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            var callNo = Interlocked.Increment(ref _deleteCalls);
            if (callNo == 1)
                throw new InvalidOperationException("delete-failure-once");

            return _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
        }
    }
}
