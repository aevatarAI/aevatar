using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

internal sealed class MassTransitKafkaEnvelopeTransport : IMassTransitEnvelopeTransport
{
    private readonly Lazy<ITopicProducer<KafkaStreamEnvelopeMessage>> _producer;
    private readonly MassTransitKafkaEnvelopeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public MassTransitKafkaEnvelopeTransport(
        IServiceProvider serviceProvider,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
        : this(
            () => serviceProvider.GetRequiredService<ITopicProducer<KafkaStreamEnvelopeMessage>>(),
            dispatcher)
    {
    }

    internal MassTransitKafkaEnvelopeTransport(
        ITopicProducer<KafkaStreamEnvelopeMessage> producer,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
        : this(() => producer, dispatcher)
    {
    }

    private MassTransitKafkaEnvelopeTransport(
        Func<ITopicProducer<KafkaStreamEnvelopeMessage>> resolveProducer,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
    {
        _producer = new Lazy<ITopicProducer<KafkaStreamEnvelopeMessage>>(resolveProducer);
        _dispatcher = dispatcher;
    }

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

        return _producer.Value.Produce(message, ct);
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        Func<MassTransitEnvelopeRecord, Task> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _dispatcher.SubscribeAsync(handler);
    }
}
