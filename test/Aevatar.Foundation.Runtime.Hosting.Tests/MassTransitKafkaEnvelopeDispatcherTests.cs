using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class MassTransitKafkaEnvelopeDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_WhenHandlerThrows_ShouldPropagateError()
    {
        var dispatcher = new MassTransitKafkaEnvelopeDispatcher();
        await dispatcher.SubscribeAsync(_ => throw new InvalidOperationException("boom"));

        Func<Task> act = () => dispatcher.DispatchAsync(CreateRecord("stream"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");
    }

    [Fact]
    public async Task DispatchAsync_WhenOneHandlerFails_ShouldStillInvokeOtherHandlers()
    {
        var dispatcher = new MassTransitKafkaEnvelopeDispatcher();
        var invokedCount = 0;

        await dispatcher.SubscribeAsync(_ =>
        {
            Interlocked.Increment(ref invokedCount);
            return Task.CompletedTask;
        });
        await dispatcher.SubscribeAsync(_ => throw new InvalidOperationException("failed"));

        Func<Task> act = () => dispatcher.DispatchAsync(CreateRecord("stream"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("failed");
        invokedCount.Should().Be(1);
    }

    private static MassTransitEnvelopeRecord CreateRecord(string streamId) =>
        new()
        {
            StreamNamespace = "aevatar.actor.events",
            StreamId = streamId,
            Payload = [1, 2, 3],
        };
}
