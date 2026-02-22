using System.Collections.Concurrent;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.KafkaAdapter;

internal sealed class OrleansKafkaQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly QueueId _queueId;
    private readonly IStreamQueueMapper _queueMapper;
    private readonly IKafkaEnvelopeTransport _transport;
    private readonly string _topicName;
    private readonly string _actorEventNamespace;
    private readonly ConcurrentQueue<IBatchContainer> _messages = new();
    private long _sequence;
    private IAsyncDisposable? _subscription;

    public OrleansKafkaQueueAdapterReceiver(
        QueueId queueId,
        IStreamQueueMapper queueMapper,
        IKafkaEnvelopeTransport transport,
        string topicName,
        string actorEventNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorEventNamespace);
        _queueId = queueId;
        _queueMapper = queueMapper;
        _transport = transport;
        _topicName = topicName;
        _actorEventNamespace = actorEventNamespace;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        _ = timeout;

        _subscription = await _transport.SubscribeAsync(async record =>
        {
            if (record.Payload is not { Length: > 0 })
                return;

            if (!string.Equals(record.StreamNamespace, _actorEventNamespace, StringComparison.Ordinal))
            {
                return;
            }

            var streamId = StreamId.Create(record.StreamNamespace, record.StreamId);
            var mapped = _queueMapper.GetQueueForStream(streamId);
            if (mapped != _queueId)
                return;

            EventEnvelope envelope;
            try
            {
                envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
            }
            catch
            {
                return;
            }

            var sequence = Interlocked.Increment(ref _sequence);
            var token = new EventSequenceTokenV2(sequence);
            _messages.Enqueue(new OrleansKafkaBatchContainer(streamId, envelope, token));
            await Task.CompletedTask;
        });
    }

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        var count = Math.Max(1, maxCount);
        IList<IBatchContainer> result = new List<IBatchContainer>(count);
        while (result.Count < count && _messages.TryDequeue(out var message))
        {
            result.Add(message);
        }

        return Task.FromResult(result);
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        _ = messages;
        return Task.CompletedTask;
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        _ = timeout;
        _ = _topicName;

        if (_subscription == null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }
}
