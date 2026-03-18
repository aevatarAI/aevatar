using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

[GenerateSerializer]
internal sealed class OrleansMassTransitBatchContainer : IBatchContainer
{
    [Id(0)]
    public StreamId StreamId { get; private set; } = default!;

    [Id(1)]
    public EventEnvelope Envelope { get; private set; } = default!;

    [Id(2)]
    public EventSequenceTokenV2 BatchSequenceToken { get; private set; } = default!;

    private OrleansMassTransitBatchContainer()
    {
    }

    public OrleansMassTransitBatchContainer(
        StreamId streamId,
        EventEnvelope envelope,
        EventSequenceTokenV2 sequenceToken)
    {
        StreamId = streamId;
        Envelope = envelope;
        BatchSequenceToken = sequenceToken;
    }

    public StreamSequenceToken SequenceToken => BatchSequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        if (typeof(T) != typeof(EventEnvelope))
            return [];

        return [Tuple.Create((T)(object)Envelope, SequenceToken)];
    }

    public bool ImportRequestContext() => false;
}
