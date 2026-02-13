// BDD: Unified event pipeline behavior.

using Aevatar.EventModules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "EventPipeline")]
public class EventPipelineBddTests
{
    [Fact(DisplayName = "Given a hardcoded agent, matching event should execute handler")]
    public async Task Given_HardcodedAgent_When_MatchingEvent_Then_HandlerExecutes()
    {
        var agent = new CounterAgent();
        agent.SetId("pipe-1");
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 10 }));
        agent.State.Count.Should().Be(10);
    }

    [Fact(DisplayName = "Given unmatched event, agent should ignore silently")]
    public async Task Given_UnmatchedEvent_Then_Ignored()
    {
        var agent = new CounterAgent();
        agent.SetId("pipe-2");
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "hi" }));
        agent.State.Count.Should().Be(0);
    }

    [Fact(DisplayName = "Given agent with module, both module and handler should execute")]
    public async Task Given_Module_When_Event_Then_BothExecute()
    {
        var agent = new CounterAgent();
        agent.SetId("pipe-3");
        var module = new TestModule();
        agent.RegisterModule(module);
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 1 }));
        agent.State.Count.Should().Be(1);
        module.InvocationCount.Should().Be(1);
    }
}

/// <summary>Test module.</summary>
public class TestModule : IEventModule
{
    public string Name => "test_module";
    public int Priority { get; init; } = 5;
    public int InvocationCount { get; private set; }
    public bool CanHandle(EventEnvelope envelope) => true;
    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    { InvocationCount++; return Task.CompletedTask; }
}
