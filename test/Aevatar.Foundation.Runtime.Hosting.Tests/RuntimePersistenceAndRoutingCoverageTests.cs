using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Routing;
using FluentAssertions;

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

        (await store.AppendAsync("actor-1", events, expectedVersion: 0)).Should().Be(2);
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
    public async Task InMemoryRouterStore_ShouldSaveLoadAndDeleteHierarchy()
    {
        var store = new InMemoryRouterStore();
        var hierarchy = new RouterHierarchy(
            ParentId: "parent-1",
            ChildrenIds: new HashSet<string>(StringComparer.Ordinal) { "child-1", "child-2" });

        (await store.LoadAsync("missing")).Should().BeNull();

        await store.SaveAsync("actor-1", hierarchy);
        var loaded = await store.LoadAsync("actor-1");
        loaded.Should().NotBeNull();
        loaded!.ParentId.Should().Be("parent-1");
        loaded.ChildrenIds.Should().BeEquivalentTo(["child-1", "child-2"]);

        await store.DeleteAsync("actor-1");
        (await store.LoadAsync("actor-1")).Should().BeNull();
    }

    [Fact]
    public async Task EventRouter_ShouldRouteByDirection_AndSkipAlreadyPublishedTargets()
    {
        var router = new EventRouter("middle");
        router.ActorId.Should().Be("middle");
        router.SetParent("parent");
        router.ParentId.Should().Be("parent");
        router.AddChild("child-1");
        router.AddChild("child-2");

        var handledCount = 0;
        var sent = new List<string>();

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Self),
            _ =>
            {
                handledCount++;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        sent.Should().BeEmpty();

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Up),
            _ =>
            {
                handledCount++;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Down),
            _ =>
            {
                handledCount++;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Both),
            _ =>
            {
                handledCount++;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        var parentCountBeforeSkip = sent.Count(x => x == "parent");
        var child1CountBeforeSkip = sent.Count(x => x == "child-1");
        var child2CountBeforeSkip = sent.Count(x => x == "child-2");

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Both, publishers: "parent,child-1"),
            _ =>
            {
                handledCount++;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        handledCount.Should().Be(5);
        sent.Should().Contain("parent");
        sent.Should().Contain("child-1");
        sent.Should().Contain("child-2");
        sent.Count(x => x == "parent").Should().Be(parentCountBeforeSkip);
        sent.Count(x => x == "child-1").Should().Be(child1CountBeforeSkip);
        sent.Count(x => x == "child-2").Should().Be(child2CountBeforeSkip + 1);

        router.RemoveChild("child-1");
        router.ChildrenIds.Should().BeEquivalentTo(["child-2"]);
        router.ClearParent();
        router.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task EventRouter_WhenEnvelopeAlreadyContainsSelfInPublisherChain_ShouldSkipHandling()
    {
        var router = new EventRouter("actor-a");
        router.SetParent("parent");
        router.AddChild("child");

        var handled = false;
        var sent = new List<string>();

        await router.RouteAsync(
            CreateEnvelope(EventDirection.Both, publishers: "actor-a"),
            _ =>
            {
                handled = true;
                return Task.CompletedTask;
            },
            (target, _) =>
            {
                sent.Add(target);
                return Task.CompletedTask;
            });

        handled.Should().BeFalse();
        sent.Should().BeEmpty();
    }

    private static EventEnvelope CreateEnvelope(EventDirection direction, string? publishers = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Direction = direction,
        };

        if (!string.IsNullOrWhiteSpace(publishers))
            envelope.Metadata[PublisherChainMetadata.PublishersMetadataKey] = publishers;

        return envelope;
    }

    private sealed class TestState
    {
        public int Count { get; init; }

        public string Name { get; init; } = string.Empty;
    }
}
