using MassTransit;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

internal sealed class MassTransitKafkaEnvelopeTransport(
    ITopicProducer<KafkaStreamEnvelopeMessage> producer,
    KafkaEnvelopeDispatcher dispatcher) : IKafkaEnvelopeTransport
{
    public Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(payload);

        var message = new KafkaStreamEnvelopeMessage
        {
            StreamNamespace = streamNamespace,
            StreamId = streamId,
            Payload = payload,
        };

        return producer.Produce(message, ct);
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        Func<KafkaEnvelopeRecord, Task> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return dispatcher.SubscribeAsync(handler);
    }
}
