using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.Persistence;
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

public class AgentContextPropagatorTests
{
    [Fact]
    public void InjectAndExtract_Roundtrip_PreservesValues()
    {
        var context = new AsyncLocalAgentContext();
        context.Set("traceId", "trace-1");
        context.Set("tenant", "tenant-a");

        var envelope = TestHelper.Envelope(new PingEvent { Message = "hello" });
        AgentContextPropagator.Inject(context, envelope);
        var extracted = AgentContextPropagator.Extract(envelope);

        extracted.Get<string>("traceId").Should().Be("trace-1");
        extracted.Get<string>("tenant").Should().Be("tenant-a");
    }

    [Fact]
    public void Extract_IgnoresMetadataWithoutContextPrefix()
    {
        var envelope = TestHelper.Envelope(new PingEvent { Message = "hello" });
        envelope.Metadata["plain"] = "value";
        envelope.Metadata["__ctx_correlationId"] = "corr-1";

        var extracted = AgentContextPropagator.Extract(envelope);

        extracted.Get<string>("correlationId").Should().Be("corr-1");
        extracted.Get<string>("plain").Should().BeNull();
    }
}

public class LocalActorRuntimeTests : IAsyncLifetime
{
    private ServiceProvider _serviceProvider = null!;
    private IActorRuntime _runtime = null!;
    private IAgentManifestStore _manifestStore = null!;
    private IStreamForwardingRegistry _forwardingRegistry = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        _serviceProvider = services.BuildServiceProvider();
        _runtime = _serviceProvider.GetRequiredService<IActorRuntime>();
        _manifestStore = _serviceProvider.GetRequiredService<IAgentManifestStore>();
        _forwardingRegistry = _serviceProvider.GetRequiredService<IStreamForwardingRegistry>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var id in new[] { "parent-1", "child-1", "restored-1", "root-t", "mid-t", "leaf-t" })
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
        binding.DirectionFilter.SetEquals([EventDirection.Down, EventDirection.Both]).Should().BeTrue();

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
            EventDirection.Both,
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
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);

        await ((GAgentBase)root.Agent).EventPublisher.PublishAsync(
            new PingEvent { Message = "transit" },
            EventDirection.Down,
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
    public async Task RestoreAll_CreatesActorFromManifest()
    {
        var agentId = "restored-1";
        await _manifestStore.SaveAsync(agentId, new AgentManifest
        {
            AgentId = agentId,
            AgentTypeName = typeof(CollectorAgent).AssemblyQualifiedName!,
        });

        await _runtime.RestoreAllAsync();

        var restored = await _runtime.GetAsync(agentId);
        restored.Should().NotBeNull();
        restored!.Id.Should().Be(agentId);
        restored.Agent.Should().BeOfType<CollectorAgent>();
    }
}
