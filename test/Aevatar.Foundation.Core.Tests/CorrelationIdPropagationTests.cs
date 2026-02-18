using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class CorrelationIdPropagationTests
{
    [Fact]
    public async Task GAgentBase_PublishAsync_PropagatesInboundCorrelationId()
    {
        var publisher = new CapturingPublisher();
        var agent = new PublishFromHandlerAgent
        {
            EventPublisher = publisher,
        };
        agent.SetId("corr-publish-agent");

        var envelope = TestHelper.Envelope(new PingEvent { Message = "ping" });
        envelope.CorrelationId = "corr-publish-1";

        await agent.HandleEventAsync(envelope);

        publisher.LastPublishCorrelationId.ShouldBe("corr-publish-1");
    }

    [Fact]
    public async Task GAgentBase_SendToAsync_PropagatesInboundCorrelationId()
    {
        var publisher = new CapturingPublisher();
        var agent = new SendFromHandlerAgent
        {
            EventPublisher = publisher,
        };
        agent.SetId("corr-send-agent");

        var envelope = TestHelper.Envelope(new PingEvent { Message = "ping" });
        envelope.CorrelationId = "corr-send-1";

        await agent.HandleEventAsync(envelope);

        publisher.LastSendCorrelationId.ShouldBe("corr-send-1");
    }

    [Fact]
    public async Task LocalActorPublisher_SendTo_WritesCorrelationIdIntoEnvelope()
    {
        var streams = new InMemoryStreamProvider();
        var publisher = new LocalActorPublisher("source-actor", new EventRouter("source-actor"), streams);
        var received = new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var _ = await streams.GetStream("target-actor").SubscribeAsync<EventEnvelope>(envelope =>
        {
            received.TrySetResult(envelope);
            return Task.CompletedTask;
        });

        await publisher.SendToAsync(
            "target-actor",
            new PingEvent { Message = "payload" },
            correlationId: "corr-envelope-1");

        var outgoing = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        outgoing.CorrelationId.ShouldBe("corr-envelope-1");
    }

    private sealed class PublishFromHandlerAgent : GAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandlePing(PingEvent evt) =>
            PublishAsync(new PongEvent { Reply = evt.Message }, EventDirection.Down);
    }

    private sealed class SendFromHandlerAgent : GAgentBase<CounterState>
    {
        [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
        public Task HandlePing(PingEvent evt) =>
            SendToAsync("target-actor", new PongEvent { Reply = evt.Message });
    }

    private sealed class CapturingPublisher : IEventPublisher
    {
        public string? LastPublishCorrelationId { get; private set; }
        public string? LastSendCorrelationId { get; private set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            string? correlationId = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastPublishCorrelationId = correlationId;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            string? correlationId = null)
            where TEvent : Google.Protobuf.IMessage
        {
            LastSendCorrelationId = correlationId;
            return Task.CompletedTask;
        }
    }
}
