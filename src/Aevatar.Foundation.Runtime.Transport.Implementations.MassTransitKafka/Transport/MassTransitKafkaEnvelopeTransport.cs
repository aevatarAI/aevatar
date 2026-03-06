using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

internal sealed class MassTransitKafkaEnvelopeTransport : IMassTransitEnvelopeTransport, IDisposable
{
    private readonly Lazy<ITopicProducer<KafkaStreamEnvelopeMessage>> _producer;
    private readonly MassTransitKafkaEnvelopeDispatcher _dispatcher;
    private IServiceScope? _producerScope;

    [ActivatorUtilitiesConstructor]
    public MassTransitKafkaEnvelopeTransport(
        IServiceScopeFactory scopeFactory,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
        : this(
            () =>
            {
                var scope = scopeFactory.CreateScope();
                return (scope, scope.ServiceProvider.GetRequiredService<ITopicProducer<KafkaStreamEnvelopeMessage>>());
            },
            dispatcher)
    {
    }

    internal MassTransitKafkaEnvelopeTransport(
        ITopicProducer<KafkaStreamEnvelopeMessage> producer,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
        : this(() => (null, producer), dispatcher)
    {
    }

    private MassTransitKafkaEnvelopeTransport(
        Func<(IServiceScope? Scope, ITopicProducer<KafkaStreamEnvelopeMessage> Producer)> resolveProducer,
        MassTransitKafkaEnvelopeDispatcher dispatcher)
    {
        _producer = new Lazy<ITopicProducer<KafkaStreamEnvelopeMessage>>(() =>
        {
            var (scope, producer) = resolveProducer();
            _producerScope = scope;
            return producer;
        });
        _dispatcher = dispatcher;
    }

    public void Dispose()
    {
        _producerScope?.Dispose();
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
