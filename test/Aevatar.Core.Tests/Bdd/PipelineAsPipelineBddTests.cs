// ─────────────────────────────────────────────────────────────
// BDD: Agent as Pipeline node behavior
// Feature: Agent acts as replaceable processing node in streaming pipeline
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Shouldly;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tests.Bdd;

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

    [Fact(DisplayName = "Given a module-driven transformer Agent, when module is replaced, behavior should change")]
    public async Task Given_ModuleDrivenAgent_When_ModuleSwapped_Then_BehaviorChanges()
    {
        // Given: Empty Agent + Module A (always replies "A")
        var published = new List<IMessage>();
        var agent = new EmptyAgent();
        agent.SetId("transformer-2");
        agent.EventPublisher = new CollectingPublisher(published);

        agent.RegisterModule(new ReplyModule("reply_a", "A"));

        // When: Module A handles event
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "test" }));

        // Then: Reply A
        published.ShouldHaveSingleItem();
        ((PongEvent)published[0]).Reply.ShouldBe("A");

        // When: Replace with Module B
        published.Clear();
        agent.SetModules([new ReplyModule("reply_b", "B")]);
        await agent.HandleEventAsync(TestHelper.Envelope(new PingEvent { Message = "test" }));

        // Then: Reply B (behavior has changed)
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
        var agent2 = new EmptyAgent();
        agent2.SetId("node-b");
        agent2.EventPublisher = new CollectingPublisher(pub2);
        agent2.RegisterModule(new ReplyModule("alt_pong", "alt-pong"));

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

public class PingToPongAgent : GAgentBase<CounterState>
{
    [EventHandler]
    public async Task Handle(PingEvent evt)
    {
        await PublishAsync(new PongEvent { Reply = $"pong:{evt.Message}" });
    }
}

// ─── Test Module: Fixed reply ───

public class ReplyModule : IEventModule
{
    private readonly string _reply;
    public string Name { get; }
    public int Priority => 0;

    public ReplyModule(string name, string reply)
    {
        Name = name;
        _reply = reply;
    }

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(PingEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        await ctx.PublishAsync(new PongEvent { Reply = _reply }, ct: ct);
    }
}

// ─── Publisher that collects published events ───

public class CollectingPublisher : IEventPublisher
{
    private readonly List<IMessage> _published;
    public CollectingPublisher(List<IMessage> published) => _published = published;

    public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
        where TEvent : IMessage
    {
        _published.Add(evt);
        return Task.CompletedTask;
    }

    public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct)
        where TEvent : IMessage
    {
        _published.Add(evt);
        return Task.CompletedTask;
    }
}
