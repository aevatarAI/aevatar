using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class KafkaEnvelopeDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_WhenHandlerThrows_ShouldPropagateError()
    {
        var dispatcher = new KafkaEnvelopeDispatcher();
        await dispatcher.SubscribeAsync(_ => throw new InvalidOperationException("boom"));

        Func<Task> act = () => dispatcher.DispatchAsync(CreateRecord("stream"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("boom");
    }

    [Fact]
    public async Task DispatchAsync_WhenOneHandlerFails_ShouldStillInvokeOtherHandlers()
    {
        var dispatcher = new KafkaEnvelopeDispatcher();
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

    private static KafkaEnvelopeRecord CreateRecord(string streamId) =>
        new()
        {
            StreamNamespace = "aevatar.actor.events",
            StreamId = streamId,
            Payload = [1, 2, 3],
        };
}
