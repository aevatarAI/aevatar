using System.Collections.Concurrent;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class MassTransitKafkaStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider, IStreamCacheManager
{
    private readonly IKafkaEnvelopeTransport _transport;
    private readonly MassTransitKafkaStreamOptions _options;
    private readonly ConcurrentDictionary<string, MassTransitKafkaStream> _streams = new(StringComparer.Ordinal);

    public MassTransitKafkaStreamProvider(
        IKafkaEnvelopeTransport transport,
        MassTransitKafkaStreamOptions options)
    {
        _transport = transport;
        _options = options;
    }

    public IStream GetStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return _streams.GetOrAdd(actorId, id =>
            new MassTransitKafkaStream(id, _options.StreamNamespace, _transport));
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _streams.TryRemove(actorId, out _);
    }
}
