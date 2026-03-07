// ─────────────────────────────────────────────────────────────
// BDD: Agent as Pipeline node behavior
// Feature: Agent acts as replaceable processing node in streaming pipeline
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Attributes;
using Shouldly;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests.Bdd;

[Trait("Category", "BDD")]
[Trait("Feature", "PipelineNode")]
public class PipelineAsPipelineBddTests
{
    [Fact(DisplayName = "Given a hardcoded transformer Agent, when receiving input event, should produce transformed output event")]
    public async Task Given_HardcodedTransformer_When_Input_Then_Output()
    {
        // Given: A transformer that converts Ping to Pong
        var published = new List<IMessage>();
        var agent = new PingToPongAgent();
        agent.SetId("transformer-1");
        agent.EventPublisher = new CollectingPublisher(published);

        // When
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "hello" }));

        // Then: Should publish a PongEvent
        published.ShouldHaveSingleItem();
        published[0].ShouldBeOfType<PongEvent>()
            .Reply.ShouldBe("pong:hello");
    }

    [Fact(DisplayName = "Given two static transformer Agents, behavior should be replaceable at composition boundary")]
    public async Task Given_TwoStaticTransformerAgents_Then_BehaviorCanBeSwapped()
    {
        var published = new List<IMessage>();
        var agent = new FixedReplyAgent("B");
        agent.SetId("transformer-2");
        agent.EventPublisher = new CollectingPublisher(published);
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "test" }));

        published.ShouldHaveSingleItem();
        ((PongEvent)published[0]).Reply.ShouldBe("B");
    }

    [Fact(DisplayName = "Given two Agents implementing same event contract, they should be interchangeable as pipeline nodes")]
    public async Task Given_TwoAgentsWithSameContract_Then_Interchangeable()
    {
        // Given: Two different implementations both handling PingEvent → PongEvent
        var pub1 = new List<IMessage>();
        var agent1 = new PingToPongAgent();
        agent1.SetId("node-a");
        agent1.EventPublisher = new CollectingPublisher(pub1);

        var pub2 = new List<IMessage>();
        var agent2 = new FixedReplyAgent("alt-pong");
        agent2.SetId("node-b");
        agent2.EventPublisher = new CollectingPublisher(pub2);

        var envelope = TestHelper.Envelope(new PingEvent { Message = "x" });

        // When: Both Agents handle the same event
        await agent1.HandleEventAsync(envelope);
        await agent2.HandleEventAsync(envelope);

        // Then: Both produce PongEvent (different content, same contract)
        pub1.ShouldHaveSingleItem().ShouldBeOfType<PongEvent>();
        pub2.ShouldHaveSingleItem().ShouldBeOfType<PongEvent>();
    }
}

// ─── Test Agent: Ping → Pong hardcoded transformation ───

public class PingToPongAgent : TestGAgentBase<CounterState>
{
    [EventHandler]
    public async Task Handle(PingEvent evt)
    {
        await PublishAsync(new PongEvent { Reply = $"pong:{evt.Message}" });
    }
}

public class FixedReplyAgent : TestGAgentBase<CounterState>
{
    private readonly string _reply;

    public FixedReplyAgent(string reply)
    {
        _reply = reply;
    }

    [EventHandler]
    public async Task Handle(PingEvent evt)
    {
        _ = evt;
        await PublishAsync(new PongEvent { Reply = _reply });
    }
}

// ─── Publisher that collects published events ───

public class CollectingPublisher : IEventPublisher
{
    private readonly List<IMessage> _published;
    public CollectingPublisher(List<IMessage> published) => _published = published;

    public Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        _published.Add(evt);
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        _published.Add(evt);
        return Task.CompletedTask;
    }
}
