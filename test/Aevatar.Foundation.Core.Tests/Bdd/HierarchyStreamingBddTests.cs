// BDD: multi-agent hierarchy with stream communication.

using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "HierarchyStreaming")]
public class HierarchyStreamingBddTests : IAsyncLifetime
{
    private IActorRuntime _runtime = null!;
    private ServiceProvider _sp = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        _sp = services.BuildServiceProvider();
        _runtime = _sp.GetRequiredService<IActorRuntime>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var id in new[] { "p1", "c1", "coord", "w1", "w2", "w3", "p4", "m4", "c4" })
            await _runtime.DestroyAsync(id);
        _sp.Dispose();
    }

    [Fact(DisplayName = "Given parent-child link, child should receive parent's Down event")]
    public async Task ParentDownToChild()
    {
        var parent = await _runtime.CreateAsync<EchoAgent>("p1");
        var child = await _runtime.CreateAsync<CollectorAgent>("c1");
        await _runtime.LinkAsync("p1", "c1");

        await ((GAgentBase)parent.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "hello" }, EventDirection.Down);
        var childAgent = (CollectorAgent)child.Agent;
        await childAgent.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2));
        childAgent.ReceivedMessages.Should().Contain("hello");
    }

    [Fact(DisplayName = "Given three workers, all should receive coordinator Down broadcast")]
    public async Task FanOut()
    {
        var coord = await _runtime.CreateAsync<EchoAgent>("coord");
        var w1 = await _runtime.CreateAsync<CollectorAgent>("w1");
        var w2 = await _runtime.CreateAsync<CollectorAgent>("w2");
        var w3 = await _runtime.CreateAsync<CollectorAgent>("w3");
        await _runtime.LinkAsync("coord", "w1");
        await _runtime.LinkAsync("coord", "w2");
        await _runtime.LinkAsync("coord", "w3");

        await ((GAgentBase)coord.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "task" }, EventDirection.Down);
        var worker1 = (CollectorAgent)w1.Agent;
        var worker2 = (CollectorAgent)w2.Agent;
        var worker3 = (CollectorAgent)w3.Agent;
        await Task.WhenAll(
            worker1.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2)),
            worker2.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2)),
            worker3.WaitForMessageCountAsync(1, TimeSpan.FromSeconds(2)));

        worker1.ReceivedMessages.Should().Contain("task");
        worker2.ReceivedMessages.Should().Contain("task");
        worker3.ReceivedMessages.Should().Contain("task");
    }

    [Fact(DisplayName = "Self event should not propagate to parent or child")]
    public async Task SelfDoesNotPropagate()
    {
        var parent = await _runtime.CreateAsync<CollectorAgent>("p4");
        var middle = await _runtime.CreateAsync<EchoAgent>("m4");
        var child = await _runtime.CreateAsync<CollectorAgent>("c4");
        await _runtime.LinkAsync("p4", "m4");
        await _runtime.LinkAsync("m4", "c4");

        await ((GAgentBase)middle.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "self" }, EventDirection.Self);
        var parentCollector = (CollectorAgent)parent.Agent;
        var childCollector = (CollectorAgent)child.Agent;

        var parentUnexpected = () => parentCollector.WaitForMessageCountAsync(1, TimeSpan.FromMilliseconds(200));
        var childUnexpected = () => childCollector.WaitForMessageCountAsync(1, TimeSpan.FromMilliseconds(200));
        await parentUnexpected.Should().ThrowAsync<TimeoutException>();
        await childUnexpected.Should().ThrowAsync<TimeoutException>();
        parentCollector.ReceivedMessages.Should().BeEmpty();
        childCollector.ReceivedMessages.Should().BeEmpty();
    }
}
