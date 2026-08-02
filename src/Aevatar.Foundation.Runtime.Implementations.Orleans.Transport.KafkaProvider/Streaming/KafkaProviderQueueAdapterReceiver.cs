using System.Diagnostics;
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
    private readonly KafkaProviderTransportOptions _transportOptions;
    private readonly string _actorEventNamespace;
    private readonly KafkaReceiverMessageBuffer _messageBuffer;
    private readonly Lock _stateLock = new();
    private readonly Lock _lifecycleLock = new();
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, Task> _ensureTransportReadyAsync;
    private readonly Func<IKafkaReceiverConsumer> _consumerFactory;
    private readonly Func<Action, Task> _ownerLoopStarter;

    private readonly HashSet<long> _inflightOffsets = [];
    private readonly HashSet<long> _ackedOffsets = [];
    private readonly TopicPartition _topicPartition;

    private long _sequence;
    private long _lastCommittedOffset;
    private bool _hasCommitCursor;
    private bool _commitDirty;
    private bool _backpressureActive;
    private bool _partitionPaused;
    private long? _pauseStartedTimestamp;
    private int _reportedPausedPartitionCount = -1;
    private int _shuttingDown;

    private ReceiverLifecycleGeneration? _currentGeneration;

    internal int BufferedMessageCount => _messageBuffer.Depth;

    public KafkaProviderQueueAdapterReceiver(
        QueueId queueId,
        string providerName,
        KafkaProviderProducer producer,
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        string actorEventNamespace,
        ILoggerFactory? loggerFactory = null)
        : this(
            queueId,
            providerName,
            transportOptions,
            mapper,
            actorEventNamespace,
            producer.StartAsync,
            () => CreateConsumer(transportOptions, providerName, mapper.GetPartitionId(queueId)),
            loggerFactory)
    {
    }

    internal KafkaProviderQueueAdapterReceiver(
        QueueId queueId,
        string providerName,
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        string actorEventNamespace,
        Func<CancellationToken, Task> ensureTransportReadyAsync,
        Func<IKafkaReceiverConsumer> consumerFactory,
        ILoggerFactory? loggerFactory = null,
        Func<Action, Task>? ownerLoopStarter = null)
    {
        ArgumentNullException.ThrowIfNull(transportOptions);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(ensureTransportReadyAsync);
        ArgumentNullException.ThrowIfNull(consumerFactory);
        transportOptions.ValidateReceiverBufferWatermarks();

        _partitionId = mapper.GetPartitionId(queueId);
        _providerName = providerName;
        _transportOptions = transportOptions;
        _actorEventNamespace = actorEventNamespace;
        _topicPartition = new TopicPartition(_transportOptions.TopicName, new Partition(_partitionId));
        _messageBuffer = new KafkaReceiverMessageBuffer(_transportOptions.ReceiverBufferCapacity);
        _ensureTransportReadyAsync = ensureTransportReadyAsync;
        _consumerFactory = consumerFactory;
        _ownerLoopStarter = ownerLoopStarter ?? (static action => Task.Run(action));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaProviderQueueAdapterReceiver>();
    }

    public Task Initialize(TimeSpan timeout)
    {
        _ = timeout;
        ReceiverLifecycleGeneration? generationToStart = null;
        lock (_lifecycleLock)
        {
            if (_currentGeneration is { ShutdownTask: null } currentGeneration)
                return currentGeneration.InitializeTask;

            var predecessorShutdownTask = _currentGeneration?.ShutdownTask ?? Task.CompletedTask;
            generationToStart = new ReceiverLifecycleGeneration(predecessorShutdownTask);
            _currentGeneration = generationToStart;
        }

        _ = CompleteInitializationAsync(generationToStart);
        return generationToStart.InitializeTask;
    }

    private async Task CompleteInitializationAsync(ReceiverLifecycleGeneration generation)
    {
        try
        {
            await AwaitCompletionAsync(generation.PredecessorShutdownTask).ConfigureAwait(false);
            generation.LifecycleCts.Token.ThrowIfCancellationRequested();

            lock (_lifecycleLock)
            {
                EnsureGenerationCanStart(generation);
                PrepareForInitialization();
            }

            await _ensureTransportReadyAsync(generation.LifecycleCts.Token).ConfigureAwait(false);
            generation.LifecycleCts.Token.ThrowIfCancellationRequested();

            var loopReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lifecycleLock)
            {
                EnsureGenerationCanStart(generation);
                var loopCts = CancellationTokenSource.CreateLinkedTokenSource(generation.LifecycleCts.Token);
                generation.ConsumeLoopCts = loopCts;
                generation.ConsumeLoopTask = _ownerLoopStarter(
                    () => ConsumeLoop(generation, loopCts.Token, loopReady));
            }

            await loopReady.Task.ConfigureAwait(false);
            generation.LifecycleCts.Token.ThrowIfCancellationRequested();

            RecordInitialBufferState();
            generation.InitializeCompletion.TrySetResult();
        }
        catch (OperationCanceledException) when (generation.LifecycleCts.IsCancellationRequested)
        {
            generation.InitializeCompletion.TrySetCanceled(generation.LifecycleCts.Token);
        }
        catch (Exception ex)
        {
            generation.InitializeCompletion.TrySetException(ex);
        }
    }

    private void EnsureGenerationCanStart(ReceiverLifecycleGeneration generation)
    {
        generation.LifecycleCts.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_currentGeneration, generation) || generation.ShutdownTask != null)
            throw new OperationCanceledException(generation.LifecycleCts.Token);
    }

    private async Task AwaitCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Kafka receiver successor generation observed predecessor cleanup failure on partition " +
                "{Partition}; the explicit generation rebuild will proceed after cleanup.",
                _partitionId);
        }
    }

    private Exception? ReadOwnerLoopFault()
    {
        var generation = Volatile.Read(ref _currentGeneration);
        return generation == null ? null : Volatile.Read(ref generation.OwnerLoopFault);
    }

    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        if (ReadOwnerLoopFault() is { } ownerLoopFault)
            return Task.FromException<IList<IBatchContainer>>(ownerLoopFault);

        var count = Math.Max(1, maxCount);
        IList<IBatchContainer> result = new List<IBatchContainer>(count);
        while (result.Count < count && _messageBuffer.TryRead(out var message))
        {
            result.Add(message!);
        }
        RecordBufferDepth();

        return Task.FromResult(result);
    }

    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        if (ReadOwnerLoopFault() is { } ownerLoopFault)
            return Task.FromException(ownerLoopFault);

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

    public Task Shutdown(TimeSpan timeout)
    {
        _ = timeout;

        ReceiverLifecycleGeneration generation;
        TaskCompletionSource? shutdownCompletion = null;
        lock (_lifecycleLock)
        {
            generation = _currentGeneration ??= ReceiverLifecycleGeneration.CreateUninitialized();
            if (generation.ShutdownTask != null)
                return generation.ShutdownTask;

            shutdownCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            generation.ShutdownTask = shutdownCompletion.Task;
        }

        _ = CompleteShutdownAsync(generation, shutdownCompletion);
        return generation.ShutdownTask;
    }

    private async Task CompleteShutdownAsync(
        ReceiverLifecycleGeneration generation,
        TaskCompletionSource shutdownCompletion)
    {
        Exception? shutdownFailure = null;

        Interlocked.Exchange(ref _shuttingDown, 1);

        try
        {
            try
            {
                generation.LifecycleCts.Cancel();
            }
            catch (Exception ex)
            {
                shutdownFailure = ex;
                _logger.LogError(ex,
                    "Kafka receiver lifecycle cancellation failed on partition {Partition}.",
                    _partitionId);
            }

            try
            {
                await generation.InitializeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (generation.LifecycleCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                shutdownFailure = Volatile.Read(ref generation.OwnerLoopFault) ?? ex;
            }

            try
            {
                generation.ConsumeLoopCts?.Cancel();
            }
            catch (Exception ex)
            {
                shutdownFailure ??= ex;
                _logger.LogError(ex,
                    "Kafka receiver owner-loop cancellation failed on partition {Partition}.",
                    _partitionId);
            }

            var loopTask = generation.ConsumeLoopTask;
            if (loopTask != null)
                await loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref generation.OwnerLoopFault) == null)
        {
        }
        catch (Exception ex)
        {
            shutdownFailure = Volatile.Read(ref generation.OwnerLoopFault) ?? ex;
        }
        finally
        {
            try
            {
                generation.ConsumeLoopCts?.Dispose();
                generation.LifecycleCts.Dispose();
                _messageBuffer.Clear();
                RecordInitialBufferState();
            }
            catch (Exception ex)
            {
                shutdownFailure ??= ex;
                _logger.LogError(ex,
                    "Kafka receiver shutdown cleanup failed on partition {Partition}.",
                    _partitionId);
            }
            if (shutdownFailure == null)
                shutdownCompletion.TrySetResult();
            else
                shutdownCompletion.TrySetException(shutdownFailure);
        }
    }

    private void ConsumeLoop(
        ReceiverLifecycleGeneration generation,
        CancellationToken ct,
        TaskCompletionSource loopReady)
    {
        IKafkaReceiverConsumer? consumer = null;

        try
        {
            consumer = TryStartConsumer(generation, ct);
            if (consumer == null)
            {
                loopReady.TrySetCanceled(ct);
                return;
            }

            ApplyBackpressure(consumer);
            loopReady.TrySetResult();

            while (!ct.IsCancellationRequested)
            {
                ApplyBackpressure(consumer);

                ConsumeResult<Ignore, byte[]>? consumeResult = null;
                try
                {
                    consumeResult = consumer.Consume(ConsumePollInterval);
                }
                catch (ConsumeException ex)
                {
                    KafkaTransportMetrics.RecordReceiverConsumeError(
                        _providerName,
                        _transportOptions.TopicName,
                        _partitionId);
                    if (ex.Error.IsFatal)
                    {
                        _logger.LogError(ex,
                            "Fatal Kafka consume error on partition {Partition}; terminating the receiver owner loop. Code={ErrorCode}",
                            _partitionId, ex.Error.Code);
                        throw;
                    }

                    ApplyBackpressure(consumer);
                    _logger.LogWarning(ex,
                        "Kafka consume error on partition {Partition}, will retry. Code={ErrorCode}",
                        _partitionId, ex.Error.Code);
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                        break;
                    continue;
                }

                // A puller can cross the low watermark while Consume is polling.
                ApplyBackpressure(consumer);

                if (consumeResult?.Message != null)
                {
                    if (!ProcessPolledRecord(consumer, consumeResult))
                        break;
                }

                try
                {
                    TryCommitContiguousOffsets(consumer);
                }
                catch (KafkaException ex) when (!ex.Error.IsFatal)
                {
                    _logger.LogWarning(ex,
                        "Kafka offset commit failed on partition {Partition}, will retry the preserved ACK watermark.",
                        _partitionId);
                }
            }
        }
        catch (Exception ex)
        {
            var ownerLoopFault = new InvalidOperationException(
                $"Kafka receiver owner loop failed on partition {_partitionId}.",
                ex);
            Volatile.Write(ref generation.OwnerLoopFault, ownerLoopFault);
            loopReady.TrySetException(ownerLoopFault);
            _logger.LogError(ownerLoopFault,
                "Kafka receiver owner loop failed on partition {Partition}.",
                _partitionId);
            throw ownerLoopFault;
        }
        finally
        {
            if (consumer != null)
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

                EndPartitionPause();
                try
                {
                    consumer.Close();
                }
                finally
                {
                    consumer.Dispose();
                }
            }

            loopReady.TrySetCanceled(ct);
        }
    }

    private IKafkaReceiverConsumer? TryStartConsumer(
        ReceiverLifecycleGeneration generation,
        CancellationToken ct)
    {
        lock (_lifecycleLock)
        {
            if (ct.IsCancellationRequested ||
                !ReferenceEquals(_currentGeneration, generation) ||
                generation.ShutdownTask != null)
            {
                return null;
            }

            var consumer = _consumerFactory();
            try
            {
                // Orleans queue balancing owns this lifecycle; each receiver is pinned to its mapped partition.
                consumer.Assign(new TopicPartitionOffset(_topicPartition, Offset.Stored));
                return consumer;
            }
            catch (Exception)
            {
                try
                {
                    consumer.Close();
                }
                catch (Exception closeException)
                {
                    _logger.LogWarning(closeException,
                        "Failed to close Kafka consumer after assignment failure on partition {Partition}.",
                        _partitionId);
                }

                try
                {
                    consumer.Dispose();
                }
                catch (Exception disposeException)
                {
                    _logger.LogWarning(disposeException,
                        "Failed to dispose Kafka consumer after assignment failure on partition {Partition}.",
                        _partitionId);
                }

                throw;
            }
        }
    }

    private bool ProcessPolledRecord(
        IKafkaReceiverConsumer consumer,
        ConsumeResult<Ignore, byte[]> consumeResult)
    {
        RegisterOffset(consumeResult.Offset.Value);
        var classification = ClassifyPolledRecord(consumeResult);
        switch (classification.Disposition)
        {
            case KafkaPolledRecordDisposition.Deliver:
            {
                var sequence = Interlocked.Increment(ref _sequence);
                var token = new EventSequenceTokenV2(sequence);
                var message = new KafkaProviderBatchContainer(
                    StreamId.Create(classification.StreamNamespace!, classification.StreamId!),
                    classification.Envelope!,
                    token,
                    consumeResult.Offset.Value);
                if (!_messageBuffer.TryWrite(message))
                {
                    consumer.Seek(consumeResult.TopicPartitionOffset);
                    _logger.LogWarning(
                        "Kafka receiver buffer reached hard capacity {Capacity} on partition {Partition}; " +
                        "rewound offset {Offset} until low-watermark recovery",
                        _messageBuffer.Capacity,
                        _partitionId,
                        consumeResult.Offset.Value);
                    return true;
                }

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

    private void TryCommitContiguousOffsets(IKafkaReceiverConsumer consumer)
    {
        long? commitCandidateInclusive = null;

        lock (_stateLock)
        {
            if (!_hasCommitCursor || !_commitDirty)
                return;

            var nextOffset = _lastCommittedOffset + 1;
            while (_ackedOffsets.Contains(nextOffset))
            {
                commitCandidateInclusive = nextOffset;
                nextOffset++;
            }
        }

        if (!commitCandidateInclusive.HasValue)
            return;

        consumer.Commit(
        [
            new TopicPartitionOffset(_topicPartition, new Offset(commitCandidateInclusive.Value + 1))
        ]);

        lock (_stateLock)
        {
            var committedInclusive = commitCandidateInclusive.Value;
            _ackedOffsets.RemoveWhere(offset => offset <= committedInclusive);
            _inflightOffsets.RemoveWhere(offset => offset <= committedInclusive);
            _lastCommittedOffset = committedInclusive;
            _commitDirty = _ackedOffsets.Count > 0;
        }
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

    private static IKafkaReceiverConsumer CreateConsumer(
        KafkaProviderTransportOptions options,
        string providerName,
        int partitionId)
    {
        var consumerBuilder = new ConsumerBuilder<Ignore, byte[]>(BuildConsumerConfig(options));
        if (options.StatisticsInterval > TimeSpan.Zero)
        {
            consumerBuilder.SetStatisticsHandler((_, statisticsJson) =>
                KafkaTransportMetrics.ObserveStatistics(
                    statisticsJson,
                    providerName,
                    options.TopicName,
                    partitionId));
        }

        return new ConfluentKafkaReceiverConsumer(consumerBuilder.Build());
    }

    private void ApplyBackpressure(IKafkaReceiverConsumer consumer)
    {
        var depth = _messageBuffer.Depth;
        if (!_backpressureActive && depth >= _transportOptions.ReceiverBufferHighWatermark)
        {
            _backpressureActive = true;
            KafkaTransportMetrics.RecordReceiverBufferSaturation(
                _providerName,
                _transportOptions.TopicName,
                _partitionId);
        }
        else if (_backpressureActive && depth <= _transportOptions.ReceiverBufferLowWatermark)
        {
            _backpressureActive = false;
        }

        if (_backpressureActive && !_partitionPaused)
        {
            consumer.Pause([_topicPartition]);
            _partitionPaused = true;
            _pauseStartedTimestamp = Stopwatch.GetTimestamp();
            KafkaTransportMetrics.RecordReceiverPauseResume(
                _providerName,
                _transportOptions.TopicName,
                _partitionId,
                KafkaTransportMetrics.PauseOperation);
        }
        else if (!_backpressureActive && _partitionPaused)
        {
            consumer.Resume([_topicPartition]);
            _partitionPaused = false;
            KafkaTransportMetrics.RecordReceiverPauseResume(
                _providerName,
                _transportOptions.TopicName,
                _partitionId,
                KafkaTransportMetrics.ResumeOperation);
            CompletePauseDuration();
        }

        RecordPausedPartitionCountIfChanged();
    }

    private void EndPartitionPause()
    {
        _partitionPaused = false;
        CompletePauseDuration();
        RecordPausedPartitionCountIfChanged();
    }

    private void PrepareForInitialization()
    {
        Interlocked.Exchange(ref _shuttingDown, 0);
        _messageBuffer.Clear();
        _sequence = 0;
        _backpressureActive = false;
        _partitionPaused = false;
        _pauseStartedTimestamp = null;
        _reportedPausedPartitionCount = -1;

        lock (_stateLock)
        {
            _inflightOffsets.Clear();
            _ackedOffsets.Clear();
            _lastCommittedOffset = 0;
            _hasCommitCursor = false;
            _commitDirty = false;
        }
    }

    private void CompletePauseDuration()
    {
        if (!_pauseStartedTimestamp.HasValue)
            return;

        KafkaTransportMetrics.RecordReceiverPauseDuration(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            Stopwatch.GetElapsedTime(_pauseStartedTimestamp.Value));
        _pauseStartedTimestamp = null;
    }

    private void RecordInitialBufferState()
    {
        RecordBufferDepth();
        KafkaTransportMetrics.RecordReceiverBufferCapacity(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            _messageBuffer.Capacity);
    }

    private void RecordBufferDepth() =>
        KafkaTransportMetrics.RecordReceiverBufferDepth(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            _messageBuffer.Depth);

    private void RecordPausedPartitionCountIfChanged()
    {
        var pausedPartitionCount = _partitionPaused ? 1 : 0;
        if (_reportedPausedPartitionCount == pausedPartitionCount)
            return;

        _reportedPausedPartitionCount = pausedPartitionCount;
        KafkaTransportMetrics.RecordReceiverPausedPartitionCount(
            _providerName,
            _transportOptions.TopicName,
            _partitionId,
            pausedPartitionCount);
    }

    private sealed class ReceiverLifecycleGeneration(Task predecessorShutdownTask)
    {
        public CancellationTokenSource LifecycleCts { get; } = new();

        public TaskCompletionSource InitializeCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeTask => InitializeCompletion.Task;

        public Task PredecessorShutdownTask { get; } = predecessorShutdownTask;

        public Task? ShutdownTask { get; set; }

        public CancellationTokenSource? ConsumeLoopCts { get; set; }

        public Task? ConsumeLoopTask { get; set; }

        public Exception? OwnerLoopFault;

        public static ReceiverLifecycleGeneration CreateUninitialized()
        {
            var generation = new ReceiverLifecycleGeneration(Task.CompletedTask);
            generation.InitializeCompletion.TrySetResult();
            return generation;
        }
    }

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
