using MassTransit;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

internal sealed class MassTransitKafkaEnvelopeTransport(
    ITopicProducer<KafkaStreamEnvelopeMessage> producer,
    MassTransitKafkaEnvelopeDispatcher dispatcher) : IMassTransitEnvelopeTransport
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
        Func<MassTransitEnvelopeRecord, Task> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return dispatcher.SubscribeAsync(handler);
    }
}
