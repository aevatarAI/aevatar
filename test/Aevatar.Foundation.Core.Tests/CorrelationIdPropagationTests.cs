using Shouldly;

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
        envelope.CorrelationId = "corr-publish-1";
        envelope.Metadata["trace.causation_id"] = "event-1";
        envelope.Metadata["tenant"] = "acme";

        await agent.HandleEventAsync(envelope);

        publisher.LastPublishSourceEnvelope.ShouldNotBeNull();
        publisher.LastPublishSourceEnvelope.CorrelationId.ShouldBe("corr-publish-1");
        publisher.LastPublishSourceEnvelope.Metadata["trace.causation_id"].ShouldBe("event-1");
        publisher.LastPublishSourceEnvelope.Metadata["tenant"].ShouldBe("acme");
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
        envelope.CorrelationId = "corr-send-1";
        envelope.Metadata["trace.causation_id"] = "event-2";
        envelope.Metadata["tenant"] = "contoso";

        await agent.HandleEventAsync(envelope);

        publisher.LastSendSourceEnvelope.ShouldNotBeNull();
        publisher.LastSendSourceEnvelope.CorrelationId.ShouldBe("corr-send-1");
        publisher.LastSendSourceEnvelope.Metadata["trace.causation_id"].ShouldBe("event-2");
        publisher.LastSendSourceEnvelope.Metadata["tenant"].ShouldBe("contoso");
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
        sourceEnvelope.CorrelationId = "corr-envelope-1";
        sourceEnvelope.Metadata["trace.causation_id"] = "event-3-ignored";
        sourceEnvelope.Metadata["tenant"] = "northwind";
        sourceEnvelope.Metadata["command.id"] = "cmd-should-not-flow";

        await publisher.SendToAsync(
            "target-actor",
            new PingEvent { Message = "payload" },
            sourceEnvelope: sourceEnvelope);

        var outgoing = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        outgoing.CorrelationId.ShouldBe("corr-envelope-1");
        outgoing.Metadata["trace.causation_id"].ShouldBe("inbound-event-3");
        outgoing.Metadata["tenant"].ShouldBe("northwind");
        outgoing.Metadata.ContainsKey("command.id").ShouldBeFalse();
    }

    private sealed class PublishFromHandlerAgent : TestGAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandlePing(PingEvent evt) =>
            PublishAsync(new PongEvent { Reply = evt.Message }, EventDirection.Down);
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
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastPublishSourceEnvelope = sourceEnvelope;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastSendSourceEnvelope = sourceEnvelope;
            return Task.CompletedTask;
        }
    }
}
