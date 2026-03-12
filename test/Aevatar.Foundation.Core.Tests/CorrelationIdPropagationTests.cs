using Shouldly;
using Aevatar.Foundation.Core.EventSourcing;

namespace Aevatar.Foundation.Core.Tests;

public class CorrelationIdPropagationTests
{
    [Fact]
    public async Task GAgentBase_PublishAsync_PropagatesInboundEnvelope()
    {
        var publisher = new CapturingPublisher();
        var agent = new PublishFromHandlerAgent
        {
            EventPublisher = publisher,
        };
        agent.SetId("corr-publish-agent");

        var envelope = TestHelper.Envelope(new PingEvent { Message = "ping" });
        envelope.EnsurePropagation().CorrelationId = "corr-publish-1";
        envelope.Propagation.CausationEventId = "event-1";
        envelope.Propagation.Baggage["tenant"] = "acme";

        await agent.HandleEventAsync(envelope);

        publisher.LastPublishSourceEnvelope.ShouldNotBeNull();
        publisher.LastPublishSourceEnvelope.Propagation.CorrelationId.ShouldBe("corr-publish-1");
        publisher.LastPublishSourceEnvelope.Propagation.CausationEventId.ShouldBe("event-1");
        publisher.LastPublishSourceEnvelope.Propagation.Baggage["tenant"].ShouldBe("acme");
    }

    [Fact]
    public async Task GAgentBase_SendToAsync_PropagatesInboundEnvelope()
    {
        var publisher = new CapturingPublisher();
        var agent = new SendFromHandlerAgent
        {
            EventPublisher = publisher,
        };
        agent.SetId("corr-send-agent");

        var envelope = TestHelper.Envelope(new PingEvent { Message = "ping" });
        envelope.EnsurePropagation().CorrelationId = "corr-send-1";
        envelope.Propagation.CausationEventId = "event-2";
        envelope.Propagation.Baggage["tenant"] = "contoso";

        await agent.HandleEventAsync(envelope);

        publisher.LastSendSourceEnvelope.ShouldNotBeNull();
        publisher.LastSendSourceEnvelope.Propagation.CorrelationId.ShouldBe("corr-send-1");
        publisher.LastSendSourceEnvelope.Propagation.CausationEventId.ShouldBe("event-2");
        publisher.LastSendSourceEnvelope.Propagation.Baggage["tenant"].ShouldBe("contoso");
    }

    [Fact]
    public async Task LocalActorPublisher_SendTo_PropagatesTraceFieldsIntoEnvelope()
    {
        var streams = new InMemoryStreamProvider();
        var publisher = new LocalActorPublisher("source-actor", new EventRouter("source-actor"), streams);
        var received = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var _ = await streams.GetStream("target-actor").SubscribeAsync<EventEnvelope>(envelope =>
        {
            received.TrySetResult(envelope);
            return Task.CompletedTask;
        });

        var sourceEnvelope = TestHelper.Envelope(new PingEvent { Message = "source" });
        sourceEnvelope.Id = "inbound-event-3";
        sourceEnvelope.EnsurePropagation().CorrelationId = "corr-envelope-1";
        sourceEnvelope.Propagation.CausationEventId = "event-3-ignored";
        sourceEnvelope.Propagation.Baggage["tenant"] = "northwind";
        sourceEnvelope.Propagation.Baggage["command.id"] = "cmd-should-not-flow";

        await publisher.SendToAsync(
            "target-actor",
            new PingEvent { Message = "payload" },
            sourceEnvelope: sourceEnvelope);

        var outgoing = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        outgoing.Propagation.CorrelationId.ShouldBe("corr-envelope-1");
        outgoing.Propagation.CausationEventId.ShouldBe("inbound-event-3");
        outgoing.Propagation.Baggage["tenant"].ShouldBe("northwind");
        outgoing.Propagation.Baggage["command.id"].ShouldBe("cmd-should-not-flow");
    }

    [Fact]
    public async Task LocalActorPublisher_PublishCommittedStateEventAsync_PropagatesTraceFieldsIntoObserverPublication()
    {
        var streams = new InMemoryStreamProvider();
        var publisher = new LocalActorPublisher("source-actor", new EventRouter("source-actor"), streams);
        var received = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var _ = await streams.GetStream("source-actor").SubscribeAsync<EventEnvelope>(envelope =>
        {
            received.TrySetResult(envelope);
            return Task.CompletedTask;
        });

        var sourceEnvelope = TestHelper.Envelope(new PingEvent { Message = "source" });
        sourceEnvelope.Id = "inbound-event-4";
        sourceEnvelope.EnsurePropagation().CorrelationId = "corr-envelope-2";
        sourceEnvelope.Propagation.Baggage["tenant"] = "fabrikam";

        await ((ICommittedStateEventPublisher)publisher).PublishAsync(
            new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new PongEvent { Reply = "committed" }),
                },
            },
            sourceEnvelope: sourceEnvelope);

        var outgoing = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        outgoing.Route.IsObserverPublication().ShouldBeTrue();
        outgoing.Runtime!.RouteTargetCount.ShouldBe(0);
        outgoing.Propagation.CorrelationId.ShouldBe("corr-envelope-2");
        outgoing.Propagation.CausationEventId.ShouldBe("inbound-event-4");
        outgoing.Propagation.Baggage["tenant"].ShouldBe("fabrikam");

        var published = outgoing.Payload!.Unpack<CommittedStateEventPublished>();
        published.StateEvent.EventData.Unpack<PongEvent>().Reply.ShouldBe("committed");
    }

    private sealed class PublishFromHandlerAgent : TestGAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandlePing(PingEvent evt) =>
            PublishAsync(new PongEvent { Reply = evt.Message }, TopologyAudience.Children);
    }

    private sealed class SendFromHandlerAgent : TestGAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandlePing(PingEvent evt) =>
            SendToAsync("target-actor", new PongEvent { Reply = evt.Message });
    }

    private sealed class CapturingPublisher : IEventPublisher
    {
        public EventEnvelope? LastPublishSourceEnvelope { get; private set; }
        public EventEnvelope? LastSendSourceEnvelope { get; private set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastPublishSourceEnvelope = sourceEnvelope;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastSendSourceEnvelope = sourceEnvelope;
            return Task.CompletedTask;
        }
    }
}
