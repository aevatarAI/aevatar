using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Confluent.Kafka;
using Aevatar.Foundation.Runtime.Observability;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal sealed class KafkaProviderQueueAdapterReceiver : IQueueAdapterReceiver
{
    private static readonly TimeSpan ConsumePollInterval = TimeSpan.FromMilliseconds(100);

    private readonly int _partitionId;
    private readonly string _providerName;
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
    public KafkaProviderQueueAdapterReceiver(
        QueueId queueId,
        string providerName,
        KafkaProviderProducer producer,
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        string actorEventNamespace,
        ILoggerFactory? loggerFactory = null)
    {
        _partitionId = mapper.GetPartitionId(queueId);
        _providerName = providerName;
        _producer = producer;
        _transportOptions = transportOptions;
        _actorEventNamespace = actorEventNamespace;
        _topicPartition = new TopicPartition(_transportOptions.TopicName, new Partition(_partitionId));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaProviderQueueAdapterReceiver>();
    }

    public Task Initialize(TimeSpan timeout)
    {
        _ = timeout;
        return InitializeAsync();
    }

    private Task InitializeAsync()
    {
        if (_consumer != null)
            return Task.CompletedTask;

        return InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        await _producer.StartAsync();

        var consumerBuilder = new ConsumerBuilder<Ignore, byte[]>(BuildConsumerConfig(_transportOptions));
        if (_transportOptions.StatisticsInterval > TimeSpan.Zero)
        {
            consumerBuilder.SetStatisticsHandler((_, statisticsJson) =>
                KafkaTransportMetrics.ObserveStatistics(
                    statisticsJson,
                    _providerName,
                    _transportOptions.TopicName,
                    _partitionId));
        }
        var consumer = consumerBuilder.Build();

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
        RecordBufferDepth();

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

        KafkaTransportMetrics.RecordReceiverBufferDepth(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            0);
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
                    if (!ProcessPolledRecord(consumeResult))
                        break;
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

    private bool ProcessPolledRecord(ConsumeResult<Ignore, byte[]> consumeResult)
    {
        RegisterOffset(consumeResult.Offset.Value);
        var classification = ClassifyPolledRecord(consumeResult);
        switch (classification.Disposition)
        {
            case KafkaPolledRecordDisposition.Deliver:
            {
                var sequence = Interlocked.Increment(ref _sequence);
                var token = new EventSequenceTokenV2(sequence);
                _messages.Enqueue(new KafkaProviderBatchContainer(
                    StreamId.Create(classification.StreamNamespace!, classification.StreamId!),
                    classification.Envelope!,
                    token,
                    consumeResult.Offset.Value));
                RecordBufferDepth();
                return true;
            }
            case KafkaPolledRecordDisposition.AcknowledgeForeignRecord:
                _logger.LogDebug(
                    "Ignoring Kafka record at offset {Offset} on partition {Partition} for foreign stream namespace {StreamNamespace}",
                    consumeResult.Offset.Value,
                    _partitionId,
                    classification.StreamNamespace);
                MarkOffsetAcknowledged(consumeResult.Offset.Value);
                return true;
            case KafkaPolledRecordDisposition.AcknowledgeInvalidRecord:
                _logger.LogWarning(
                    classification.ParseException,
                    "Invalid Kafka actor-event record at offset {Offset} on partition {Partition}. Reason={InvalidReason}; message will be skipped",
                    consumeResult.Offset.Value,
                    _partitionId,
                    classification.InvalidReason);
                AgentMetrics.RecordEnvelopeTerminalFailure(
                    AgentMetrics.FailureReasonInvalidEnvelope,
                    AgentMetrics.FailureDispositionReturned);
                MarkOffsetAcknowledged(consumeResult.Offset.Value);
                return true;
            case KafkaPolledRecordDisposition.PreserveForRedelivery:
                _logger.LogInformation(
                    "Preserving polled Kafka record at offset {Offset} on partition {Partition} for redelivery during receiver shutdown",
                    consumeResult.Offset.Value,
                    _partitionId);
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(classification.Disposition));
        }
    }

    private KafkaPolledRecordClassification ClassifyPolledRecord(
        ConsumeResult<Ignore, byte[]> consumeResult)
    {
        if (Volatile.Read(ref _shuttingDown) == 1)
            return KafkaPolledRecordClassification.PreserveForRedelivery();

        var headers = consumeResult.Message.Headers;
        var streamNamespace = TryGetHeaderValue(headers, KafkaProviderHeaderConstants.StreamNamespace);
        var streamIdValue = TryGetHeaderValue(headers, KafkaProviderHeaderConstants.StreamId);
        if (!string.IsNullOrWhiteSpace(streamNamespace) &&
            !string.Equals(streamNamespace, _actorEventNamespace, StringComparison.Ordinal))
        {
            return KafkaPolledRecordClassification.AcknowledgeForeignRecord(streamNamespace);
        }

        if (string.IsNullOrWhiteSpace(streamNamespace) || string.IsNullOrWhiteSpace(streamIdValue))
        {
            return KafkaPolledRecordClassification.AcknowledgeInvalidRecord(
                KafkaInvalidRecordReason.MissingRoutingHeaders);
        }

        if (consumeResult.Message.Value is not { Length: > 0 })
        {
            return KafkaPolledRecordClassification.AcknowledgeInvalidRecord(
                KafkaInvalidRecordReason.EmptyPayload);
        }

        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(consumeResult.Message.Value);
        }
        catch (InvalidProtocolBufferException ex)
        {
            return KafkaPolledRecordClassification.AcknowledgeInvalidRecord(
                KafkaInvalidRecordReason.ProtobufParseFailed,
                ex);
        }

        return KafkaPolledRecordClassification.Deliver(streamNamespace, streamIdValue, envelope);
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

    internal static ConsumerConfig BuildConsumerConfig(KafkaProviderTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.ConsumerGroup,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true,
            StatisticsIntervalMs = options.StatisticsInterval > TimeSpan.Zero
                ? Math.Max(1, (int)Math.Min(int.MaxValue, options.StatisticsInterval.TotalMilliseconds))
                : 0,
        };
    }

    private void RecordBufferDepth() =>
        KafkaTransportMetrics.RecordReceiverBufferDepth(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            _messages.Count);

    private enum KafkaPolledRecordDisposition
    {
        Deliver,
        AcknowledgeForeignRecord,
        AcknowledgeInvalidRecord,
        PreserveForRedelivery,
    }

    private enum KafkaInvalidRecordReason
    {
        None,
        MissingRoutingHeaders,
        EmptyPayload,
        ProtobufParseFailed,
    }

    private sealed record KafkaPolledRecordClassification(
        KafkaPolledRecordDisposition Disposition,
        string? StreamNamespace = null,
        string? StreamId = null,
        EventEnvelope? Envelope = null,
        KafkaInvalidRecordReason InvalidReason = KafkaInvalidRecordReason.None,
        Exception? ParseException = null)
    {
        public static KafkaPolledRecordClassification Deliver(
            string streamNamespace,
            string streamId,
            EventEnvelope envelope) =>
            new(KafkaPolledRecordDisposition.Deliver, streamNamespace, streamId, envelope);

        public static KafkaPolledRecordClassification AcknowledgeForeignRecord(string streamNamespace) =>
            new(KafkaPolledRecordDisposition.AcknowledgeForeignRecord, StreamNamespace: streamNamespace);

        public static KafkaPolledRecordClassification AcknowledgeInvalidRecord(
            KafkaInvalidRecordReason reason,
            Exception? parseException = null) =>
            new(
                KafkaPolledRecordDisposition.AcknowledgeInvalidRecord,
                InvalidReason: reason,
                ParseException: parseException);

        public static KafkaPolledRecordClassification PreserveForRedelivery() =>
            new(KafkaPolledRecordDisposition.PreserveForRedelivery);
    }
}
