using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal sealed class KafkaProviderQueueAdapterReceiver : IQueueAdapterReceiver
{
    private static readonly TimeSpan ConsumePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly int _partitionId;
    private readonly KafkaProviderProducer _producer;
    private readonly KafkaProviderTransportOptions _transportOptions;
    private readonly string _actorEventNamespace;
    private readonly ConcurrentQueue<IBatchContainer> _messages = new();
    private readonly Lock _stateLock = new();
    private readonly ILogger _logger;

    private readonly HashSet<long> _inflightOffsets = [];
    private readonly HashSet<long> _ackedOffsets = [];
    private readonly TopicPartition _topicPartition;

    private long _sequence;
    private long _lastCommittedOffset;
    private bool _hasCommitCursor;
    private bool _commitDirty;
    private int _shuttingDown;

    private IConsumer<Ignore, byte[]>? _consumer;
    private CancellationTokenSource? _consumeLoopCts;
    private Task? _consumeLoopTask;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public KafkaProviderQueueAdapterReceiver(
        QueueId queueId,
        KafkaProviderProducer producer,
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        string actorEventNamespace,
        ILoggerFactory? loggerFactory = null)
    {
        _partitionId = mapper.GetPartitionId(queueId);
        _producer = producer;
        _transportOptions = transportOptions;
        _actorEventNamespace = actorEventNamespace;
        _topicPartition = new TopicPartition(_transportOptions.TopicName, new Partition(_partitionId));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaProviderQueueAdapterReceiver>();
    }

    public Task Initialize(TimeSpan timeout)
    {
        return InitializeAsync(timeout);
    }

    private async Task InitializeAsync(TimeSpan timeout)
    {
        await _initGate.WaitAsync(timeout);
        try
        {
            if (_initialized)
                return;

            await InitializeCoreAsync();
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task InitializeCoreAsync()
    {
        await _producer.StartAsync();

        var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _transportOptions.BootstrapServers,
            GroupId = _transportOptions.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true,
        }).Build();

        consumer.Assign(new TopicPartitionOffset(_topicPartition, Offset.Stored));
        _consumer = consumer;
        _consumeLoopCts = new CancellationTokenSource();
        _consumeLoopTask = Task.Run(() => ConsumeLoopAsync(_consumeLoopCts.Token));
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
        foreach (var message in messages.OfType<KafkaProviderBatchContainer>())
        {
            lock (_stateLock)
            {
                if (_inflightOffsets.Contains(message.KafkaOffset))
                {
                    _ackedOffsets.Add(message.KafkaOffset);
                    _commitDirty = true;
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        _ = timeout;

        if (Interlocked.Exchange(ref _shuttingDown, 1) == 1)
            return;

        var loopCts = Interlocked.Exchange(ref _consumeLoopCts, null);
        loopCts?.Cancel();

        var loopTask = Interlocked.Exchange(ref _consumeLoopTask, null);
        if (loopTask != null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        loopCts?.Dispose();

        var consumer = Interlocked.Exchange(ref _consumer, null);
        if (consumer != null)
        {
            consumer.Close();
            consumer.Dispose();
        }
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var consumer = _consumer ?? throw new InvalidOperationException("Kafka queue receiver consumer is not initialized.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<Ignore, byte[]>? consumeResult = null;
                try
                {
                    consumeResult = consumer.Consume(ConsumePollInterval);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning(ex,
                        "Kafka consume error on partition {Partition}, will retry. Code={ErrorCode}",
                        _partitionId, ex.Error.Code);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }

                if (consumeResult?.Message != null)
                {
                    RegisterOffset(consumeResult.Offset.Value);

                    var batch = TryCreateBatch(consumeResult);
                    if (batch == null)
                    {
                        MarkOffsetAcknowledged(consumeResult.Offset.Value);
                    }
                    else
                    {
                        _messages.Enqueue(batch);
                    }
                }

                TryCommitContiguousOffsets(consumer);
                await Task.Yield();
            }
        }
        finally
        {
            try
            {
                TryCommitContiguousOffsets(consumer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to commit offsets during consume loop shutdown on partition {Partition}.",
                    _partitionId);
            }
        }
    }

    private KafkaProviderBatchContainer? TryCreateBatch(ConsumeResult<Ignore, byte[]> consumeResult)
    {
        if (Volatile.Read(ref _shuttingDown) == 1 ||
            consumeResult.Message.Value is not { Length: > 0 })
        {
            return null;
        }

        var headers = consumeResult.Message.Headers;
        var streamNamespace = TryGetHeaderValue(headers, KafkaProviderHeaderConstants.StreamNamespace);
        var streamIdValue = TryGetHeaderValue(headers, KafkaProviderHeaderConstants.StreamId);
        if (!string.Equals(streamNamespace, _actorEventNamespace, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(streamIdValue))
        {
            return null;
        }

        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(consumeResult.Message.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse EventEnvelope from Kafka message at offset {Offset} on partition {Partition}. Message will be skipped.",
                consumeResult.Offset.Value, _partitionId);
            return null;
        }

        var streamId = StreamId.Create(streamNamespace!, streamIdValue);
        var sequence = Interlocked.Increment(ref _sequence);
        var token = new EventSequenceTokenV2(sequence);
        return new KafkaProviderBatchContainer(streamId, envelope, token, consumeResult.Offset.Value);
    }

    private void RegisterOffset(long offset)
    {
        lock (_stateLock)
        {
            if (!_hasCommitCursor)
            {
                _lastCommittedOffset = offset - 1;
                _hasCommitCursor = true;
            }

            _inflightOffsets.Add(offset);
        }
    }

    private void MarkOffsetAcknowledged(long offset)
    {
        lock (_stateLock)
        {
            if (_inflightOffsets.Contains(offset))
            {
                _ackedOffsets.Add(offset);
                _commitDirty = true;
            }
        }
    }

    private void TryCommitContiguousOffsets(IConsumer<Ignore, byte[]> consumer)
    {
        long? committedInclusive = null;

        lock (_stateLock)
        {
            if (!_hasCommitCursor || !_commitDirty)
                return;

            while (_ackedOffsets.Contains(_lastCommittedOffset + 1))
            {
                var nextOffset = _lastCommittedOffset + 1;
                _ackedOffsets.Remove(nextOffset);
                _inflightOffsets.Remove(nextOffset);
                _lastCommittedOffset = nextOffset;
                committedInclusive = nextOffset;
            }

            // Bound inflight/acked sets: if the gap between the earliest inflight
            // and lastCommitted is too large, drop stale entries to prevent unbounded growth.
            if (_inflightOffsets.Count > 0)
            {
                var minInflight = long.MaxValue;
                foreach (var o in _inflightOffsets)
                {
                    if (o < minInflight) minInflight = o;
                }

                const int maxInflightGap = 100_000;
                if (_inflightOffsets.Count > maxInflightGap)
                {
                    // Evict offsets that are far behind the watermark — they'll never be acked.
                    var evictionThreshold = minInflight + maxInflightGap;
                    _inflightOffsets.RemoveWhere(o => o > evictionThreshold && !_ackedOffsets.Contains(o));
                    _ackedOffsets.RemoveWhere(o => !_inflightOffsets.Contains(o));
                    _logger.LogWarning(
                        "Evicted stale inflight offsets on partition {Partition}. InflightCount={InflightCount}, AckedCount={AckedCount}.",
                        _partitionId, _inflightOffsets.Count, _ackedOffsets.Count);
                }
            }

            _commitDirty = _ackedOffsets.Count > 0;
        }

        if (!committedInclusive.HasValue)
            return;

        consumer.Commit(
        [
            new TopicPartitionOffset(_topicPartition, new Offset(committedInclusive.Value + 1))
        ]);
    }

    private static string? TryGetHeaderValue(Headers headers, string name)
    {
        var header = headers.LastOrDefault(x => string.Equals(x.Key, name, StringComparison.Ordinal));
        if (header == null)
            return null;

        var bytes = header.GetValueBytes();
        return bytes.Length == 0
            ? string.Empty
            : System.Text.Encoding.UTF8.GetString(bytes);
    }
}
