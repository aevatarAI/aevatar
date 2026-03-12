using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public class RunManagerTests
{
    [Fact]
    public void StartNewRun_SameScope_CancelsPreviousRun()
    {
        var manager = new RunManager();

        var first = manager.StartNewRun("scope-a");
        first.IsCancelled.Should().BeFalse();

        var second = manager.StartNewRun("scope-a");

        first.IsCancelled.Should().BeTrue();
        second.IsCancelled.Should().BeFalse();
        manager.GetCurrentRun("scope-a").Should().BeSameAs(second);
    }

    [Fact]
    public void CancelRun_RemovesAndCancelsCurrentRun()
    {
        var manager = new RunManager();
        var run = manager.StartNewRun("scope-b");

        manager.CancelRun("scope-b");

        run.IsCancelled.Should().BeTrue();
        manager.GetCurrentRun("scope-b").Should().BeNull();
    }

    [Fact]
    public void RunContextScope_Begin_RestoresPreviousRunOnDispose()
    {
        var outer = new RunContext();
        var inner = new RunContext();

        using (RunContextScope.Begin(outer))
        {
            RunContextScope.CurrentRun.Should().BeSameAs(outer);
            using (RunContextScope.Begin(inner))
            {
                RunContextScope.CurrentRun.Should().BeSameAs(inner);
            }

            RunContextScope.CurrentRun.Should().BeSameAs(outer);
        }

        RunContextScope.CurrentRun.Should().BeNull();
    }
}

public class AsyncLocalAgentContextTests
{
    [Fact]
    public void GetAll_ShouldSnapshotCurrentValues()
    {
        var context = new AsyncLocalAgentContext();
        context.Set("traceId", "trace-1");
        context.Set("tenant", "tenant-a");

        var snapshot = context.GetAll();

        snapshot.Should().ContainKey("traceId").WhoseValue.Should().Be("trace-1");
        snapshot.Should().ContainKey("tenant").WhoseValue.Should().Be("tenant-a");
    }

    [Fact]
    public void Remove_ShouldDropExistingValue()
    {
        var context = new AsyncLocalAgentContext();
        context.Set("correlationId", "corr-1");

        context.Remove("correlationId");

        context.Get<string>("correlationId").Should().BeNull();
    }
}

public class LocalActorRuntimeTests : IAsyncLifetime
{
    private ServiceProvider _serviceProvider = null!;
    private IActorRuntime _runtime = null!;
    private IStreamForwardingRegistry _forwardingRegistry = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        _serviceProvider = services.BuildServiceProvider();
        _runtime = _serviceProvider.GetRequiredService<IActorRuntime>();
        _forwardingRegistry = _serviceProvider.GetRequiredService<IStreamForwardingRegistry>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var id in new[] { "parent-1", "child-1", "restored-1", "root-t", "mid-t", "leaf-t", "collector-dedup" })
            await _runtime.DestroyAsync(id);

        _serviceProvider.Dispose();
    }

    [Fact]
    public async Task LinkAndUnlink_UpdatesParentAndChildrenRelationship()
    {
        var parent = await _runtime.CreateAsync<EchoAgent>("parent-1");
        var child = await _runtime.CreateAsync<CollectorAgent>("child-1");

        await _runtime.LinkAsync(parent.Id, child.Id);

        (await child.GetParentIdAsync()).Should().Be(parent.Id);
        (await parent.GetChildrenIdsAsync()).Should().Contain(child.Id);

        await _runtime.UnlinkAsync(child.Id);

        (await child.GetParentIdAsync()).Should().BeNull();
        (await parent.GetChildrenIdsAsync()).Should().NotContain(child.Id);
    }

    [Fact]
    public async Task LinkAndUnlink_ShouldMaintainStreamForwardingBinding()
    {
        var parent = await _runtime.CreateAsync<EchoAgent>("parent-1");
        var child = await _runtime.CreateAsync<CollectorAgent>("child-1");

        await _runtime.LinkAsync(parent.Id, child.Id);

        var bindings = await _forwardingRegistry.ListBySourceAsync(parent.Id, CancellationToken.None);
        var binding = bindings.Should().ContainSingle(x =>
            x.TargetStreamId == child.Id &&
            x.ForwardingMode == StreamForwardingMode.HandleThenForward).Subject;
        binding.DirectionFilter.SetEquals([BroadcastDirection.Down, BroadcastDirection.Both]).Should().BeTrue();

        await _runtime.UnlinkAsync(child.Id);

        var afterUnlink = await _forwardingRegistry.ListBySourceAsync(parent.Id, CancellationToken.None);
        afterUnlink.Should().BeEmpty();
    }

    [Fact]
    public async Task ChildBothEvent_ShouldReachParentMailbox()
    {
        var parent = await _runtime.CreateAsync<CollectorAgent>("parent-1");
        var child = await _runtime.CreateAsync<CollectorAgent>("child-1");
        await _runtime.LinkAsync(parent.Id, child.Id);

        await ((GAgentBase)child.Agent).EventPublisher.PublishAsync(
            new PingEvent { Message = "child-both" },
            BroadcastDirection.Both,
            CancellationToken.None);

        var parentCollector = (CollectorAgent)parent.Agent;
        await parentCollector.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2));
        parentCollector.ReceivedMessages.Should().Contain("child-both");
    }

    [Fact]
    public async Task TransitOnlyBinding_ShouldSkipIntermediateActorHandling_AndKeepTransitToLeaf()
    {
        var root = await _runtime.CreateAsync<EchoAgent>("root-t");
        var middle = await _runtime.CreateAsync<CollectorAgent>("mid-t");
        var leaf = await _runtime.CreateAsync<CollectorAgent>("leaf-t");
        await _runtime.LinkAsync(root.Id, middle.Id);
        await _runtime.LinkAsync(middle.Id, leaf.Id);

        await _forwardingRegistry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = root.Id,
                TargetStreamId = middle.Id,
                ForwardingMode = StreamForwardingMode.TransitOnly,
                DirectionFilter =
                [
                    BroadcastDirection.Down,
                    BroadcastDirection.Both,
                ],
            },
            CancellationToken.None);

        await ((GAgentBase)root.Agent).EventPublisher.PublishAsync(
            new PingEvent { Message = "transit" },
            BroadcastDirection.Down,
            CancellationToken.None);

        var middleCollector = (CollectorAgent)middle.Agent;
        var leafCollector = (CollectorAgent)leaf.Agent;
        await leafCollector.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2));

        var middleUnexpected = () => middleCollector.WaitForMessageCountAsync(1, TimeSpan.FromMilliseconds(200));
        await middleUnexpected.Should().ThrowAsync<TimeoutException>();
        leafCollector.ReceivedMessages.Should().Contain("transit");
        middleCollector.ReceivedMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenActorExists_ShouldReturnActor()
    {
        var agentId = "restored-1";
        await _runtime.CreateAsync<CollectorAgent>(agentId);

        var restored = await _runtime.GetAsync(agentId);
        restored.Should().NotBeNull();
        restored!.Id.Should().Be(agentId);
        restored.Agent.Should().BeOfType<CollectorAgent>();
    }

    [Fact]
    public async Task HandleEventAsync_ShouldDeduplicateByStableOriginId_ForSameActor()
    {
        const string actorId = "collector-dedup";
        var actor = await _runtime.CreateAsync<CollectorAgent>(actorId);
        var collector = (CollectorAgent)actor.Agent;
        var first = TestHelper.Envelope(new PingEvent { Message = "dup" }, publisherId: "source-1");
        first.Id = "env-1";
        first.EnsureRuntime().EnsureDeduplication().OperationId = "logical-dedup-1";

        var second = TestHelper.Envelope(new PingEvent { Message = "dup" }, publisherId: "source-1");
        second.Id = "env-2";
        second.EnsureRuntime().EnsureDeduplication().OperationId = "logical-dedup-1";

        await actor.HandleEventAsync(first);
        await actor.HandleEventAsync(second);

        collector.ReceivedMessages.Should().ContainSingle().Which.Should().Be("dup");
    }
}
