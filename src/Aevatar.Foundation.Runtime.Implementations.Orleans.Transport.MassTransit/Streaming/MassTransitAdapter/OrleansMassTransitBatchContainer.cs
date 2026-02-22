using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

internal sealed class OrleansMassTransitBatchContainer : IBatchContainer
{
    private readonly EventEnvelope _envelope;

    public OrleansMassTransitBatchContainer(
        StreamId streamId,
        EventEnvelope envelope,
        EventSequenceTokenV2 sequenceToken)
    {
        StreamId = streamId;
        _envelope = envelope;
        SequenceToken = sequenceToken;
    }

    public StreamId StreamId { get; }

    public StreamSequenceToken SequenceToken { get; }

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        if (typeof(T) != typeof(EventEnvelope))
            return [];

        return [Tuple.Create((T)(object)_envelope, SequenceToken)];
    }

    public bool ImportRequestContext() => false;
}
