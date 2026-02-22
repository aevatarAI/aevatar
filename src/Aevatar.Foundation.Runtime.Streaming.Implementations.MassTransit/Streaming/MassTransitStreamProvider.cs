using System.Collections.Concurrent;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider
{
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly MassTransitStreamOptions _options;
    private readonly ConcurrentDictionary<string, MassTransitStream> _streams = new(StringComparer.Ordinal);

    public MassTransitStreamProvider(
        IMassTransitEnvelopeTransport transport,
        MassTransitStreamOptions options)
    {
        _transport = transport;
        _options = options;
    }

    public IStream GetStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return _streams.GetOrAdd(actorId, id =>
            new MassTransitStream(id, _options.StreamNamespace, _transport));
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _streams.TryRemove(actorId, out _);
    }
}
