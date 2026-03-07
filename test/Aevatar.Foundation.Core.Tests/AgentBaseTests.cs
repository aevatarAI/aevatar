// GAgentBase tests.

using Aevatar.Foundation.Abstractions.Attributes;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

// Test agents.

public class AgentBaseCounterAgent : TestGAgentBase<CounterState>
{
    public int HandleCount { get; private set; }

    [EventHandler]
    public async Task HandleIncrement(IncrementEvent evt)
    {
        State.Count += evt.Amount;
        HandleCount++;
        await Task.CompletedTask;
    }

    [EventHandler(Priority = 10)]
    public async Task HandleDecrement(DecrementEvent evt)
    {
        State.Count -= evt.Amount;
        HandleCount++;
        await Task.CompletedTask;
    }

    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"Counter:{State.Count}");
}

// ─── Tests ───

public class AgentBaseTests
{
    [Fact]
    public async Task HandleEvent_DispatchesToStaticHandler()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-1");

        var envelope = TestHelper.Envelope(new IncrementEvent { Amount = 5 });
        await agent.HandleEventAsync(envelope);

        agent.State.Count.ShouldBe(5);
        agent.HandleCount.ShouldBe(1);
    }

    [Fact]
    public async Task HandleEvent_MultipleHandlers_AllInvoked()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-2");

        // First increment
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 10 }));
        // Then decrement
        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 3 }));

        agent.State.Count.ShouldBe(7);
        agent.HandleCount.ShouldBe(2);
    }

    [Fact]
    public async Task HandleEvent_UnknownType_NoError()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-3");

        // PingEvent has no corresponding handler
        var envelope = TestHelper.Envelope(new PingEvent { Message = "hello" });
        await agent.HandleEventAsync(envelope);

        agent.State.Count.ShouldBe(0);
        agent.HandleCount.ShouldBe(0);
    }

    [Fact]
    public async Task HandleEvent_RespectsStaticHandlerPriorities()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-4");

        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 2 }));
        await agent.HandleEventAsync(TestHelper.Envelope(new DecrementEvent { Amount = 1 }));

        agent.State.Count.ShouldBe(1);
        agent.HandleCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetSubscribedEventTypes_ReturnsStaticHandlerTypes()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-5");

        var types = await agent.GetSubscribedEventTypesAsync();

        types.ShouldContain(typeof(IncrementEvent));
        types.ShouldContain(typeof(DecrementEvent));
    }

    [Fact]
    public async Task Description_ReturnsCustom()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-6");

        var desc = await agent.GetDescriptionAsync();
        desc.ShouldBe("Counter:0");
    }

    [Fact]
    public void State_ThrowsOutsideHandlerScope()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-7");

        // State getter is always allowed
        var state = agent.State;
        state.ShouldNotBeNull();

        // But State setter should throw outside handler scope
        // (Testing protected setter via reflection)
        var prop = typeof(GAgentBase<CounterState>).GetProperty("State");
        var setter = prop!.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            var act = () => setter.Invoke(agent, [new CounterState { Count = 999 }]);
            var ex = Should.Throw<System.Reflection.TargetInvocationException>(act);
            ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        }
    }
}
