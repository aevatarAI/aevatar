// BDD: Unified event pipeline behavior.

using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests.Bdd;

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

    [Fact(DisplayName = "Given agent with static handler, matching event should update state deterministically")]
    public async Task Given_StaticHandler_When_Event_Then_StateTransitions()
    {
        var agent = new CounterAgent();
        agent.SetId("pipe-3");
        await agent.HandleEventAsync(TestHelper.Envelope(new IncrementEvent { Amount = 1 }));
        agent.State.Count.Should().Be(1);
    }
}
