using MassTransit;
using Microsoft.Extensions.Logging;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

internal sealed class MassTransitKafkaEnvelopeConsumer(
    MassTransitKafkaEnvelopeDispatcher dispatcher,
    ILogger<MassTransitKafkaEnvelopeConsumer> logger) : IConsumer<KafkaStreamEnvelopeMessage>
{
    public async Task Consume(ConsumeContext<KafkaStreamEnvelopeMessage> context)
    {
        var message = context.Message;
        if (string.IsNullOrWhiteSpace(message.StreamNamespace) ||
            string.IsNullOrWhiteSpace(message.StreamId) ||
            message.Payload is not { Length: > 0 })
        {
            logger.LogWarning("Skip empty kafka stream envelope.");
            return;
        }

        var record = new MassTransitEnvelopeRecord
        {
            StreamNamespace = message.StreamNamespace,
            StreamId = message.StreamId,
            Payload = message.Payload,
        };

        try
        {
            await dispatcher.DispatchAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Kafka envelope dispatch failed for stream {StreamNamespace}/{StreamId}.",
                message.StreamNamespace,
                message.StreamId);
            throw;
        }
    }
}
