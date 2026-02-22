using MassTransit;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

internal sealed class MassTransitKafkaEnvelopeConsumer(
    KafkaEnvelopeDispatcher dispatcher,
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

        var record = new KafkaEnvelopeRecord
        {
            StreamNamespace = message.StreamNamespace,
            StreamId = message.StreamId,
            Payload = message.Payload,
        };

        await dispatcher.DispatchAsync(record);
    }
}
