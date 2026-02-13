// ─── InMemoryStateStore + InMemoryEventStore tests ───

using Aevatar.Helpers;
using Shouldly;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tests;

public class InMemoryStateStoreTests
{
    [Fact]
    public async Task LoadSaveDelete_Roundtrip()
    {
        var store = new InMemoryStateStore<CounterState>();

        // Initially null
        var loaded = await store.LoadAsync("agent-1");
        loaded.ShouldBeNull();

        // Save
        await store.SaveAsync("agent-1", new CounterState { Count = 42, Name = "test" });

        // Load
        loaded = await store.LoadAsync("agent-1");
        loaded.ShouldNotBeNull();
        loaded!.Count.ShouldBe(42);
        loaded.Name.ShouldBe("test");

        // Delete
        await store.DeleteAsync("agent-1");
        loaded = await store.LoadAsync("agent-1");
        loaded.ShouldBeNull();
    }
}

public class InMemoryEventStoreTests
{
    [Fact]
    public async Task AppendAndGet_Roundtrip()
    {
        var store = new InMemoryEventStore();

        var events = new[]
        {
            new StateEvent
            {
                EventId = "e1",
                Timestamp = TimestampHelper.Now(),
                Version = 1,
                EventType = "test",
                AgentId = "agent-1",
            },
            new StateEvent
            {
                EventId = "e2",
                Timestamp = TimestampHelper.Now(),
                Version = 2,
                EventType = "test",
                AgentId = "agent-1",
            },
        };

        var version = await store.AppendAsync("agent-1", events, 0);
        version.ShouldBe(2);

        var loaded = await store.GetEventsAsync("agent-1");
        loaded.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetVersion_ReturnsLatest()
    {
        var store = new InMemoryEventStore();

        var version = await store.GetVersionAsync("non-existent");
        version.ShouldBe(0);

        await store.AppendAsync("a1",
        [
            new StateEvent { EventId = "e1", Version = 1, AgentId = "a1" }
        ], 0);

        version = await store.GetVersionAsync("a1");
        version.ShouldBe(1);
    }

    [Fact]
    public async Task OptimisticConcurrency_ThrowsOnConflict()
    {
        var store = new InMemoryEventStore();

        await store.AppendAsync("a1",
            [new StateEvent { EventId = "e1", Version = 1, AgentId = "a1" }], 0);

        // Append with wrong expectedVersion
        Func<Task> act = () => store.AppendAsync("a1",
            [new StateEvent { EventId = "e2", Version = 2, AgentId = "a1" }],
            0); // Should be 1

        await Should.ThrowAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task GetEvents_FromVersion_FiltersCorrectly()
    {
        var store = new InMemoryEventStore();

        var events = Enumerable.Range(1, 5).Select(i => new StateEvent
        {
            EventId = $"e{i}",
            Version = i,
            AgentId = "a1",
        }).ToList();

        await store.AppendAsync("a1", events, 0);

        var fromV3 = await store.GetEventsAsync("a1", fromVersion: 3);
        fromV3.Count.ShouldBe(2);
        fromV3[0].Version.ShouldBe(4);
        fromV3[1].Version.ShouldBe(5);
    }
}
