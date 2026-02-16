// BDD: Event handler hook behavior.

using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "Hooks")]
public class HooksBddTests
{
    [Fact(DisplayName = "Given agent, hooks should be called before and after handler")]
    public async Task HooksCalledAroundHandler()
    {
        var agent = new HookTrackingAgent();
        agent.SetId("hook-1");
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 1 }));

        agent.HookStartCalled.Should().BeTrue();
        agent.HookEndCalled.Should().BeTrue();
        agent.LastDuration.Should().NotBeNull();
    }

    [Fact(DisplayName = "Given handler throws, OnEnd hook should receive exception")]
    public async Task HooksReceiveException()
    {
        var agent = new ThrowingAgent();
        agent.SetId("hook-2");
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 1 }));

        agent.LastException.Should().NotBeNull();
    }
}

/// <summary>Test agent that tracks hook invocation.</summary>
public class HookTrackingAgent : GAgentBase<CounterState>
{
    public bool HookStartCalled { get; private set; }
    public bool HookEndCalled { get; private set; }
    public TimeSpan? LastDuration { get; private set; }

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public Task Handle(IncrementEvent evt) { State.Count += evt.Amount; return Task.CompletedTask; }

    protected override Task OnEventHandlerStartAsync(EventEnvelope e, string h, object? p, CancellationToken ct)
    { HookStartCalled = true; return Task.CompletedTask; }

    protected override Task OnEventHandlerEndAsync(EventEnvelope e, string h, object? p, TimeSpan d, Exception? ex, CancellationToken ct)
    { HookEndCalled = true; LastDuration = d; return Task.CompletedTask; }
}

/// <summary>Test agent whose handler throws an exception.</summary>
public class ThrowingAgent : GAgentBase<CounterState>
{
    public Exception? LastException { get; private set; }

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public Task Handle(IncrementEvent evt) => throw new InvalidOperationException("test error");

    protected override Task OnEventHandlerEndAsync(EventEnvelope e, string h, object? p, TimeSpan d, Exception? ex, CancellationToken ct)
    { LastException = ex; return Task.CompletedTask; }
}
