namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public interface IMassTransitEnvelopeTransport
{
    Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default)
    ;

    Task<IAsyncDisposable> SubscribeAsync(
        Func<MassTransitEnvelopeRecord, Task> handler,
        CancellationToken ct = default);
}
