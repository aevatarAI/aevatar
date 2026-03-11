using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

internal sealed class OrleansMassTransitQueueAdapter : IQueueAdapter
{
    private readonly string _providerName;
    private readonly Lazy<IMassTransitEnvelopeTransport> _transport;
    private readonly IStreamQueueMapper _queueMapper;
    private readonly string _actorEventNamespace;

    public OrleansMassTransitQueueAdapter(
        string providerName,
        IMassTransitEnvelopeTransport transport,
        IStreamQueueMapper queueMapper,
        string actorEventNamespace)
        : this(providerName, () => transport, queueMapper, actorEventNamespace)
    {
    }

    public OrleansMassTransitQueueAdapter(
        string providerName,
        Func<IMassTransitEnvelopeTransport> resolveTransport,
        IStreamQueueMapper queueMapper,
        string actorEventNamespace)
    {
        _providerName = providerName;
        _transport = new Lazy<IMassTransitEnvelopeTransport>(resolveTransport);
        _queueMapper = queueMapper;
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
                    Route = new EnvelopeRoute
                    {
                        Direction = EventDirection.Down,
                    },
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
        new OrleansMassTransitQueueAdapterReceiver(
            queueId,
            _queueMapper,
            () => _transport.Value,
            _actorEventNamespace);
}
