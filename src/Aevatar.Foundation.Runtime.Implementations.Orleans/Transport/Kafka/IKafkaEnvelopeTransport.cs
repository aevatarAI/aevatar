namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

public interface IKafkaEnvelopeTransport
{
    Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default);

    Task<IAsyncDisposable> SubscribeAsync(
        Func<KafkaEnvelopeRecord, Task> handler,
        CancellationToken ct = default);
}
