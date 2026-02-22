using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.KafkaAdapter;

internal sealed class OrleansKafkaQueueAdapter : IQueueAdapter
{
    private readonly string _providerName;
    private readonly string _topicName;
    private readonly IKafkaEnvelopeTransport _transport;
    private readonly IStreamQueueMapper _queueMapper;

    public OrleansKafkaQueueAdapter(
        string providerName,
        string topicName,
        IKafkaEnvelopeTransport transport,
        IStreamQueueMapper queueMapper)
    {
        _providerName = providerName;
        _topicName = topicName;
        _transport = transport;
        _queueMapper = queueMapper;
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
                    Direction = EventDirection.Down,
                },
                _ => null,
            };

            if (envelope == null)
                continue;

            var streamNamespace = streamId.GetNamespace() ?? OrleansRuntimeConstants.ActorEventStreamNamespace;
            await _transport.PublishAsync(
                streamNamespace,
                streamId.GetKeyAsString(),
                envelope.ToByteArray(),
                CancellationToken.None);
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new OrleansKafkaQueueAdapterReceiver(queueId, _queueMapper, _transport, _topicName);
}
