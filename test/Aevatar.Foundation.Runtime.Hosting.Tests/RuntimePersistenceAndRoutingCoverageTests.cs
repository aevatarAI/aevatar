using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimePersistenceAndRoutingCoverageTests
{
    [Fact]
    public async Task InMemoryStateStore_ShouldRoundtripSaveLoadAndDelete()
    {
        var store = new InMemoryStateStore<TestState>();

        (await store.LoadAsync("actor-1")).Should().BeNull();

        await store.SaveAsync("actor-1", new TestState { Count = 7, Name = "n1" });
        var loaded = await store.LoadAsync("actor-1");
        loaded.Should().NotBeNull();
        loaded!.Count.Should().Be(7);
        loaded.Name.Should().Be("n1");

        await store.DeleteAsync("actor-1");
        (await store.LoadAsync("actor-1")).Should().BeNull();
    }

    [Fact]
    public async Task InMemoryEventStore_ShouldAppendQueryVersionAndCheckOptimisticConcurrency()
    {
        var store = new InMemoryEventStore();
        StateEvent[] events =
        [
            new StateEvent { EventId = "e1", Version = 1, EventType = "test", AgentId = "actor-1" },
            new StateEvent { EventId = "e2", Version = 2, EventType = "test", AgentId = "actor-1" },
        ];

        var commitResult = await store.AppendAsync("actor-1", events, expectedVersion: 0);
        commitResult.LatestVersion.Should().Be(2);
        commitResult.CommittedEvents.Select(x => x.Version).Should().Equal(1, 2);
        (await store.GetVersionAsync("actor-1")).Should().Be(2);
        (await store.GetVersionAsync("missing")).Should().Be(0);

        var all = await store.GetEventsAsync("actor-1");
        all.Select(x => x.Version).Should().Equal(1, 2);

        var fromVersionOne = await store.GetEventsAsync("actor-1", fromVersion: 1);
        fromVersionOne.Select(x => x.Version).Should().Equal(2);

        (await store.GetEventsAsync("missing")).Should().BeEmpty();

        Func<Task> conflict = () => store.AppendAsync(
            "actor-1",
            [new StateEvent { EventId = "e3", Version = 3, AgentId = "actor-1" }],
            expectedVersion: 1);
        await conflict.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LocalActorRuntime_LinkAndUnlink_ShouldMaintainTopologyState()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);

        var parent = await runtime.CreateAsync<CoverageTestAgent>("parent");
        var child = await runtime.CreateAsync<CoverageTestAgent>("child");

        await runtime.LinkAsync(parent.Id, child.Id);
        (await parent.GetChildrenIdsAsync()).Should().BeEquivalentTo(["child"]);
        (await child.GetParentIdAsync()).Should().Be("parent");

        await runtime.UnlinkAsync(child.Id);
        (await parent.GetChildrenIdsAsync()).Should().BeEmpty();
        (await child.GetParentIdAsync()).Should().BeNull();
    }

    private sealed class TestState
    {
        public int Count { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class CoverageTestAgent : IAgent
    {
        public string Id => "coverage";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("coverage");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
