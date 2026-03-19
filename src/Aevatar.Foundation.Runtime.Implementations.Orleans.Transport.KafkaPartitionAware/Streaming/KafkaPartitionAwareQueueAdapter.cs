using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionAwareQueueAdapter : IQueueAdapter
{
    private readonly string _providerName;
    private readonly Lazy<IKafkaPartitionAwareEnvelopeTransport> _transport;
    private readonly Func<LocalPartitionRecordRouter> _resolveLocalRouter;
    private readonly StrictQueuePartitionMapper _mapper;
    private readonly string _actorEventNamespace;

    public KafkaPartitionAwareQueueAdapter(
        string providerName,
        Func<IKafkaPartitionAwareEnvelopeTransport> resolveTransport,
        Func<LocalPartitionRecordRouter> resolveLocalRouter,
        StrictQueuePartitionMapper mapper,
        string actorEventNamespace)
    {
        _providerName = providerName;
        _transport = new Lazy<IKafkaPartitionAwareEnvelopeTransport>(resolveTransport);
        _resolveLocalRouter = resolveLocalRouter;
        _mapper = mapper;
        _actorEventNamespace = actorEventNamespace;
    }

    public string Name => _providerName;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public async Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        _ = token;
        _ = requestContext;

        foreach (var evt in events)
        {
            var envelope = evt switch
            {
                EventEnvelope eventEnvelope => eventEnvelope,
                IMessage protobuf => new EventEnvelope
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Payload = Any.Pack(protobuf),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
                },
                _ => null,
            };

            if (envelope == null)
                continue;

            var streamNamespace = streamId.GetNamespace() ?? OrleansRuntimeConstants.ActorEventStreamNamespace;
            await _transport.Value.PublishAsync(
                streamNamespace,
                streamId.GetKeyAsString(),
                envelope.ToByteArray(),
                CancellationToken.None);
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new KafkaPartitionAwareQueueAdapterReceiver(
            queueId,
            _resolveLocalRouter,
            _mapper,
            _actorEventNamespace);
}
