using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionAwareQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly QueueId _queueId;
    private readonly int _partitionId;
    private readonly Lazy<LocalPartitionRecordRouter> _localRouter;
    private readonly string _actorEventNamespace;
    private readonly ConcurrentQueue<IBatchContainer> _messages = new();
    private readonly ConcurrentDictionary<KafkaPartitionAwareBatchContainer, TaskCompletionSource<bool>> _pendingDeliveries = new();
    private long _sequence;
    private IAsyncDisposable? _subscription;
    private int _shuttingDown;

    public KafkaPartitionAwareQueueAdapterReceiver(
        QueueId queueId,
        Func<LocalPartitionRecordRouter> resolveLocalRouter,
        IStrictOrleansStreamQueueMapper mapper,
        string actorEventNamespace)
    {
        _queueId = queueId;
        _partitionId = mapper.GetPartitionId(queueId);
        _localRouter = new Lazy<LocalPartitionRecordRouter>(resolveLocalRouter);
        _actorEventNamespace = actorEventNamespace;
    }

    public Task Initialize(TimeSpan timeout)
    {
        _ = timeout;
        return InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _subscription = await _localRouter.Value.SubscribeAsync(_partitionId, HandleRecordAsync);
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
        foreach (var message in messages.OfType<KafkaPartitionAwareBatchContainer>())
        {
            if (_pendingDeliveries.TryRemove(message, out var completion))
                completion.TrySetResult(true);
        }

        return Task.CompletedTask;
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        _ = timeout;

        if (Interlocked.Exchange(ref _shuttingDown, 1) == 1)
            return;

        var subscription = Interlocked.Exchange(ref _subscription, null);
        if (subscription != null)
            await subscription.DisposeAsync();

        AbortPendingDeliveries("the Orleans queue receiver is shutting down before delivery acknowledgement");
    }

    private Task HandleRecordAsync(PartitionEnvelopeRecord record)
    {
        if (Volatile.Read(ref _shuttingDown) == 1)
        {
            return Task.FromException(new PartitionRecordHandoffAbortedException(
                _partitionId,
                "the Orleans queue receiver is already shutting down"));
        }

        if (record.PartitionId != _partitionId ||
            !string.Equals(record.StreamNamespace, _actorEventNamespace, StringComparison.Ordinal) ||
            record.Payload is not { Length: > 0 })
        {
            return Task.CompletedTask;
        }

        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
        }
        catch
        {
            return Task.CompletedTask;
        }

        var streamId = StreamId.Create(record.StreamNamespace, record.StreamId);
        var sequence = Interlocked.Increment(ref _sequence);
        var token = new EventSequenceTokenV2(sequence);
        var batch = new KafkaPartitionAwareBatchContainer(streamId, envelope, token);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDeliveries.TryAdd(batch, completion);

        if (Volatile.Read(ref _shuttingDown) == 1)
        {
            if (_pendingDeliveries.TryRemove(batch, out var aborted))
            {
                aborted.TrySetException(new PartitionRecordHandoffAbortedException(
                    _partitionId,
                    "the Orleans queue receiver started shutting down before the batch was enqueued"));
            }

            return completion.Task;
        }

        _messages.Enqueue(batch);

        if (Volatile.Read(ref _shuttingDown) == 1 &&
            _pendingDeliveries.TryRemove(batch, out var completionDuringShutdown))
        {
            completionDuringShutdown.TrySetException(new PartitionRecordHandoffAbortedException(
                _partitionId,
                "the Orleans queue receiver started shutting down after the batch was enqueued"));
        }

        return completion.Task;
    }

    private void AbortPendingDeliveries(string reason)
    {
        foreach (var pending in _pendingDeliveries.Keys)
        {
            if (_pendingDeliveries.TryRemove(pending, out var completion))
            {
                completion.TrySetException(new PartitionRecordHandoffAbortedException(
                    _partitionId,
                    reason));
            }
        }
    }
}
