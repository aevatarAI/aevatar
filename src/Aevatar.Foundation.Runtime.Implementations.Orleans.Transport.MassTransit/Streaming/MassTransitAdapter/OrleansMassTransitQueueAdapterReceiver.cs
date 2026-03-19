using System.Collections.Concurrent;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Orleans.Runtime;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

internal sealed class OrleansMassTransitQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly QueueId _queueId;
    private readonly Lazy<IMassTransitEnvelopeTransport> _transport;
    private readonly IStreamQueueMapper _queueMapper;
    private readonly string _actorEventNamespace;
    private readonly ConcurrentQueue<IBatchContainer> _messages = new();
    private long _sequence;
    private IAsyncDisposable? _subscription;

    public OrleansMassTransitQueueAdapterReceiver(
        QueueId queueId,
        IMassTransitEnvelopeTransport transport,
        IStreamQueueMapper queueMapper,
        string actorEventNamespace)
        : this(queueId, () => transport, queueMapper, actorEventNamespace)
    {
    }

    public OrleansMassTransitQueueAdapterReceiver(
        QueueId queueId,
        Func<IMassTransitEnvelopeTransport> resolveTransport,
        IStreamQueueMapper queueMapper,
        string actorEventNamespace)
    {
        _queueId = queueId;
        _transport = new Lazy<IMassTransitEnvelopeTransport>(resolveTransport);
        _queueMapper = queueMapper;
        _actorEventNamespace = actorEventNamespace;
    }

    public Task Initialize(TimeSpan timeout)
    {
        _ = timeout;
        return InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _subscription = await _transport.Value.SubscribeAsync(HandleRecordAsync);
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

        if (_subscription == null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }

    private Task HandleRecordAsync(MassTransitEnvelopeRecord record)
    {
        if (!string.Equals(record.StreamNamespace, _actorEventNamespace, StringComparison.Ordinal) ||
            record.Payload is not { Length: > 0 })
            return Task.CompletedTask;

        EventEnvelope envelope;
        var streamId = StreamId.Create(record.StreamNamespace, record.StreamId);
        if (_queueMapper.GetQueueForStream(streamId) != _queueId)
            return Task.CompletedTask;

        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
        }
        catch
        {
            return Task.CompletedTask;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        var token = new EventSequenceTokenV2(sequence);
        _messages.Enqueue(new OrleansMassTransitBatchContainer(streamId, envelope, token));
        return Task.CompletedTask;
    }
}
