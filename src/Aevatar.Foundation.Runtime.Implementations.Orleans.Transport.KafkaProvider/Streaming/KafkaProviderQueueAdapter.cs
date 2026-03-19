using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal sealed class KafkaProviderQueueAdapter : IQueueAdapter
{
    private readonly string _providerName;
    private readonly KafkaProviderProducer _producer;
    private readonly KafkaProviderTransportOptions _transportOptions;
    private readonly string _actorEventNamespace;

    public KafkaProviderQueueAdapter(
        string providerName,
        KafkaProviderProducer producer,
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        string actorEventNamespace)
    {
        _providerName = providerName;
        _producer = producer;
        _transportOptions = transportOptions;
        Mapper = mapper;
        _actorEventNamespace = actorEventNamespace;
    }

    public string Name => _providerName;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    internal KafkaQueuePartitionMapper Mapper { get; }

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
            await _producer.PublishAsync(
                streamNamespace,
                streamId.GetKeyAsString(),
                envelope.ToByteArray(),
                CancellationToken.None);
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new KafkaProviderQueueAdapterReceiver(
            queueId,
            _producer,
            _transportOptions,
            Mapper,
            _actorEventNamespace);
}
