// BDD: multi-agent hierarchy with stream communication.

using Aevatar.Actor;
using Aevatar.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "HierarchyStreaming")]
public class HierarchyStreamingBddTests : IAsyncLifetime
{
    private IActorRuntime _runtime = null!;
    private ServiceProvider _sp = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStreamProvider, InMemoryStreamProvider>();
        services.AddSingleton<IActorRuntime>(sp =>
            new LocalActorRuntime(sp.GetRequiredService<IStreamProvider>(), sp));
        _sp = services.BuildServiceProvider();
        _runtime = _sp.GetRequiredService<IActorRuntime>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var all = await _runtime.GetAllAsync();
        foreach (var a in all) await _runtime.DestroyAsync(a.Id);
        _sp.Dispose();
    }

    [Fact(DisplayName = "Given parent-child link, child should receive parent's Down event")]
    public async Task ParentDownToChild()
    {
        var parent = await _runtime.CreateAsync<EchoAgent>("p1");
        var child = await _runtime.CreateAsync<CollectorAgent>("c1");
        await _runtime.LinkAsync("p1", "c1");

        await ((GAgentBase)parent.Agent).EventPublisher.PublishAsync(new PingEvent { Message = "hello" }, EventDirection.Down);
        await WaitFor(() => ((CollectorAgent)child.Agent).ReceivedMessages.Count > 0);
        ((CollectorAgent)child.Agent).ReceivedMessages.Should().Contain("hello");
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
        await WaitFor(() =>
            ((CollectorAgent)w1.Agent).ReceivedMessages.Count > 0 &&
            ((CollectorAgent)w2.Agent).ReceivedMessages.Count > 0 &&
            ((CollectorAgent)w3.Agent).ReceivedMessages.Count > 0);

        ((CollectorAgent)w1.Agent).ReceivedMessages.Should().Contain("task");
        ((CollectorAgent)w2.Agent).ReceivedMessages.Should().Contain("task");
        ((CollectorAgent)w3.Agent).ReceivedMessages.Should().Contain("task");
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
        await Task.Delay(100);

        ((CollectorAgent)parent.Agent).ReceivedMessages.Should().BeEmpty();
        ((CollectorAgent)child.Agent).ReceivedMessages.Should().BeEmpty();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Condition not met");
    }
}
