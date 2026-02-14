// GAgentBase tests.

using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Shouldly;

namespace Aevatar.Tests;

// Test agents.

public class AgentBaseCounterAgent : GAgentBase<CounterState>
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

// Test modules.

public class TestModule : IEventModule
{
    public string Name => "test_module";
    public int Priority { get; init; } = 5;
    public bool CanHandle(EventEnvelope envelope) => true;

    public int InvocationCount { get; private set; }

    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.CompletedTask;
    }
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
    public async Task Modules_ParticipateInPipeline()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-4");
        var module = new TestModule();
        agent.RegisterModule(module);

        var envelope = TestHelper.Envelope(new IncrementEvent { Amount = 1 });
        await agent.HandleEventAsync(envelope);

        // Both static handler and module should execute
        agent.State.Count.ShouldBe(1);
        module.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetModules_ReplacesExisting()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-5");

        var mod1 = new TestModule();
        var mod2 = new TestModule();
        agent.RegisterModule(mod1);
        agent.SetModules([mod2]);

        agent.GetModules().Count.ShouldBe(1);
        agent.GetModules()[0].ShouldBeSameAs(mod2);
    }

    [Fact]
    public async Task GetSubscribedEventTypes_ReturnsStaticHandlerTypes()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-6");

        var types = await agent.GetSubscribedEventTypesAsync();

        types.ShouldContain(typeof(IncrementEvent));
        types.ShouldContain(typeof(DecrementEvent));
    }

    [Fact]
    public async Task Description_ReturnsCustom()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-7");

        var desc = await agent.GetDescriptionAsync();
        desc.ShouldBe("Counter:0");
    }

    [Fact]
    public void State_ThrowsOutsideHandlerScope()
    {
        var agent = new AgentBaseCounterAgent();
        agent.SetId("test-8");

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
