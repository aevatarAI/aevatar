using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

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

    [Fact]
    public async Task LocalActorRuntime_LinkAndUnlink_ShouldRegisterAndRemoveReverseCommittedFactsRelay()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);

        var parent = await runtime.CreateAsync<CoverageTestAgent>("parent-relay");
        var child = await runtime.CreateAsync<CoverageTestAgent>("child-relay");

        await runtime.LinkAsync(parent.Id, child.Id);

        var childBindings = await registry.ListBySourceAsync(child.Id, CancellationToken.None);
        var reverseRelay = childBindings.Should().ContainSingle(x => x.TargetStreamId == parent.Id).Subject;
        reverseRelay.DirectionFilter.Should().BeEquivalentTo([TopologyAudience.Unspecified]);

        await runtime.UnlinkAsync(child.Id);

        (await registry.ListBySourceAsync(child.Id, CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync(parent.Id, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task LocalActorRuntime_DestroyAsync_ShouldRemoveReverseCommittedFactsRelays()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);

        var parent = await runtime.CreateAsync<CoverageTestAgent>("parent-destroy-relay");
        var child = await runtime.CreateAsync<CoverageTestAgent>("child-destroy-relay");
        await runtime.LinkAsync(parent.Id, child.Id);

        (await registry.ListBySourceAsync(child.Id, CancellationToken.None))
            .Should()
            .ContainSingle(x => x.TargetStreamId == parent.Id);

        await runtime.DestroyAsync(child.Id);

        (await registry.ListBySourceAsync(child.Id, CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync(parent.Id, CancellationToken.None)).Should().BeEmpty();
        (await parent.GetChildrenIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LocalActorRuntime_ShouldEmitTopologyAndDeactivateActivities()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);

        var parent = await runtime.CreateAsync<CoverageTestAgent>("parent-observed");
        var child = await runtime.CreateAsync<CoverageTestAgent>("child-observed");
        await runtime.LinkAsync(parent.Id, child.Id);
        await runtime.UnlinkAsync(child.Id);
        await runtime.DestroyAsync(child.Id);

        var link = stopped.ShouldContainActivity(
            AevatarActivitySource.AgentLinkActivityName,
            AevatarActivitySource.AgentIdTag,
            "child-observed");
        link.GetTagItem(AevatarActivitySource.AgentParentTag).Should().Be("parent-observed");

        var unlink = stopped.ShouldContainActivity(
            AevatarActivitySource.AgentUnlinkActivityName,
            AevatarActivitySource.AgentIdTag,
            "child-observed");
        unlink.GetTagItem(AevatarActivitySource.AgentParentTag).Should().Be("parent-observed");

        var deactivate = stopped.ShouldContainActivity(
            AevatarActivitySource.AgentDeactivateActivityName,
            AevatarActivitySource.AgentIdTag,
            "child-observed");
        deactivate.GetTagItem(AevatarActivitySource.AgentTypeTag)
            .Should().Be(typeof(CoverageTestAgent).AssemblyQualifiedName);
    }

    // Refactor (v1/issue1463-first):
    //   Old: non-blocking deactivation hook failure 无 regression test,行为可能被改成 blocking 而无人察觉
    //   New: 锁定 hook 抛异常不阻塞 actor lifecycle 的当前行为
    [Fact]
    public async Task LocalActorRuntime_DestroyAsync_WhenDeactivationHookDispatchFails_ShouldStillRemoveActor()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(new InMemoryStreamOptions(), NullLoggerFactory.Instance, registry);
        var hookDispatcher = new FaultedDeactivationHookDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IActorDeactivationHookDispatcher>(hookDispatcher)
            .BuildServiceProvider();
        var runtime = new LocalActorRuntime(streams, services, streams);

        await runtime.CreateAsync<CoverageTestAgent>("hook-failure-actor");

        var act = async () => await runtime.DestroyAsync("hook-failure-actor");

        await act.Should().NotThrowAsync();
        hookDispatcher.ActorIds.Should().ContainSingle().Which.Should().Be("hook-failure-actor");
        (await runtime.ExistsAsync("hook-failure-actor")).Should().BeFalse();
        (await runtime.GetAsync("hook-failure-actor")).Should().BeNull();
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

    private sealed class FaultedDeactivationHookDispatcher : IActorDeactivationHookDispatcher
    {
        public ConcurrentQueue<string> ActorIds { get; } = new();

        public Task DispatchAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Enqueue(actorId);
            return Task.FromException(new InvalidOperationException("hook-failed"));
        }
    }
}

file static class ActivityAssertions
{
    public static Activity ShouldContainActivity(
        this ConcurrentQueue<Activity> activities,
        string displayName,
        string tagName,
        string tagValue)
    {
        return activities
            .Where(activity =>
                activity.DisplayName == displayName &&
                string.Equals(
                    activity.GetTagItem(tagName) as string,
                    tagValue,
                    StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which;
    }
}
