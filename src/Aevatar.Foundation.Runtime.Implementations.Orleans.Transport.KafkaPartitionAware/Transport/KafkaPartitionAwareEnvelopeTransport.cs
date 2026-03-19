using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionAwareEnvelopeTransport :
    IKafkaPartitionAwareEnvelopeTransport,
    IHostedService,
    IAsyncDisposable
{
    private const string StreamNamespaceHeader = "aevatar-stream-namespace";
    private const string StreamIdHeader = "aevatar-stream-id";
    private static readonly TimeSpan NoHandlerRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LifecycleRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int LifecycleRetryAttempts = 3;

    private readonly KafkaPartitionAwareTransportOptions _transportOptions;
    private readonly StrictQueuePartitionMapper _mapper;
    private readonly ILogger<KafkaPartitionAwareEnvelopeTransport> _logger;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>> _recordHandlers = [];
    private List<Func<PartitionLifecycleEvent, Task>> _lifecycleHandlers = [];

    private IProducer<Null, byte[]>? _producer;
    private IConsumer<Ignore, byte[]>? _consumer;
    private CancellationTokenSource? _consumeLoopCts;
    private Task? _consumeLoopTask;
    private bool _started;

    public KafkaPartitionAwareEnvelopeTransport(
        KafkaPartitionAwareTransportOptions transportOptions,
        AevatarOrleansRuntimeOptions runtimeOptions,
        ILoggerFactory? loggerFactory = null)
    {
        _transportOptions = transportOptions;
        _mapper = new StrictQueuePartitionMapper(
            runtimeOptions.StreamProviderName,
            Math.Max(1, runtimeOptions.QueueCount));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaPartitionAwareEnvelopeTransport>();
    }

    public async Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(payload);

        await StartAsync(ct);

        var producer = _producer ?? throw new InvalidOperationException("Kafka partition-aware producer is not started.");
        var partitionId = _mapper.GetPartitionId(streamNamespace, streamId);
        var message = new Message<Null, byte[]>
        {
            Value = payload,
            Headers =
            [
                new Header(StreamNamespaceHeader, Encoding.UTF8.GetBytes(streamNamespace)),
                new Header(StreamIdHeader, Encoding.UTF8.GetBytes(streamId)),
            ],
        };

        await producer.ProduceAsync(
            new TopicPartition(_transportOptions.TopicName, new Partition(partitionId)),
            message,
            ct);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_started)
                return;

            await EnsureTopicExistsAsync(ct);

            _producer = new ProducerBuilder<Null, byte[]>(new ProducerConfig
            {
                BootstrapServers = _transportOptions.BootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true,
            }).Build();

            var consumer = new ConsumerBuilder<Ignore, byte[]>(new ConsumerConfig
            {
                BootstrapServers = _transportOptions.BootstrapServers,
                GroupId = _transportOptions.ConsumerGroup,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                AllowAutoCreateTopics = true,
            })
            .SetPartitionsAssignedHandler((IConsumer<Ignore, byte[]> _, List<TopicPartition> partitions) =>
            {
                FireLifecycleEvent(partitions, PartitionLifecycleEventKind.Assigned);
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                FireLifecycleEvent(partitions.Select(x => x.TopicPartition), PartitionLifecycleEventKind.Revoked);
            })
            .Build();

            consumer.Subscribe(_transportOptions.TopicName);
            _consumer = consumer;
            _consumeLoopCts = new CancellationTokenSource();
            _consumeLoopTask = Task.Run(() => ConsumeLoopAsync(_consumeLoopCts.Token));
            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (!_started)
                return;

            _consumeLoopCts?.Cancel();
            if (_consumeLoopTask != null)
            {
                try
                {
                    await _consumeLoopTask.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _consumer?.Close();
            _consumer?.Dispose();
            _consumer = null;

            if (_producer != null)
            {
                _producer.Flush(ct);
                _producer.Dispose();
                _producer = null;
            }

            _consumeLoopTask = null;
            _consumeLoopCts?.Dispose();
            _consumeLoopCts = null;
            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<IAsyncDisposable> SubscribePartitionLifecycleAsync(
        Func<PartitionLifecycleEvent, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var next = new List<Func<PartitionLifecycleEvent, Task>>(_lifecycleHandlers)
            {
                handler
            };
            _lifecycleHandlers = next;
        }

        return Task.FromResult<IAsyncDisposable>(new CallbackSubscription(() => RemoveLifecycleHandler(handler)));
    }

    public Task<IAsyncDisposable> SubscribePartitionRecordsAsync(
        int partitionId,
        Func<PartitionEnvelopeRecord, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var next = new Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>>(_recordHandlers);
            if (!next.TryGetValue(partitionId, out var handlers))
                handlers = [];
            else
                handlers = [.. handlers];

            handlers.Add(handler);
            next[partitionId] = handlers;
            _recordHandlers = next;
        }

        return Task.FromResult<IAsyncDisposable>(new CallbackSubscription(() => RemoveRecordHandler(partitionId, handler)));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task EnsureTopicExistsAsync(CancellationToken ct)
    {
        var expectedPartitionCount = Math.Max(1, _mapper.GetAllQueues().Count());
        if (_transportOptions.TopicPartitionCount != expectedPartitionCount)
        {
            throw new InvalidOperationException(
                $"Kafka partition-aware transport requires QueueCount == TopicPartitionCount, but QueueCount={expectedPartitionCount} and TopicPartitionCount={_transportOptions.TopicPartitionCount}.");
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _transportOptions.BootstrapServers,
        }).Build();

        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = _transportOptions.TopicName,
                    NumPartitions = expectedPartitionCount,
                    ReplicationFactor = 1,
                }
            ]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(x => x.Error.Code == Confluent.Kafka.ErrorCode.TopicAlreadyExists))
        {
        }

        var metadata = admin.GetMetadata(_transportOptions.TopicName, TimeSpan.FromSeconds(5));
        var topicMetadata = metadata.Topics.FirstOrDefault(x => string.Equals(x.Topic, _transportOptions.TopicName, StringComparison.Ordinal));
        if (topicMetadata == null)
        {
            throw new InvalidOperationException(
                $"Kafka partition-aware transport could not resolve topic metadata for '{_transportOptions.TopicName}'.");
        }

        var actualPartitionCount = topicMetadata.Partitions.Count;
        if (actualPartitionCount != expectedPartitionCount)
        {
            throw new InvalidOperationException(
                $"Kafka partition-aware transport requires QueueCount == actual topic partition count, but QueueCount={expectedPartitionCount} and topic '{_transportOptions.TopicName}' has {actualPartitionCount} partitions.");
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var consumer = _consumer ?? throw new InvalidOperationException("Kafka partition-aware consumer is not started.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<Ignore, byte[]>? consumeResult;
                try
                {
                    consumeResult = consumer.Consume(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (consumeResult?.Message == null)
                    continue;

                var record = TryCreateRecord(consumeResult);
                if (record == null)
                {
                    consumer.StoreOffset(consumeResult);
                    consumer.Commit(consumeResult);
                    continue;
                }

                try
                {
                    await DeliverToPartitionHandlersAsync(record, ct);
                }
                catch (PartitionRecordHandoffAbortedException)
                {
                    continue;
                }

                consumer.StoreOffset(consumeResult);
                consumer.Commit(consumeResult);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FireLifecycleEvent(
        IEnumerable<TopicPartition> partitions,
        PartitionLifecycleEventKind kind)
    {
        var partitionList = partitions.ToArray();
        if (partitionList.Length == 0)
            return;

        List<Func<PartitionLifecycleEvent, Task>> handlers;
        lock (_lock)
        {
            handlers = [.. _lifecycleHandlers];
        }

        if (handlers.Count == 0)
            return;

        _ = Task.Run(() => DispatchLifecycleEventsAsync(partitionList, kind, handlers));
    }

    private PartitionEnvelopeRecord? TryCreateRecord(ConsumeResult<Ignore, byte[]> consumeResult)
    {
        if (consumeResult.Message.Value is not { Length: > 0 })
            return null;

        var headers = consumeResult.Message.Headers;
        var streamNamespace = TryGetHeaderValue(headers, StreamNamespaceHeader);
        var streamId = TryGetHeaderValue(headers, StreamIdHeader);
        if (string.IsNullOrWhiteSpace(streamNamespace) || string.IsNullOrWhiteSpace(streamId))
            return null;

        return new PartitionEnvelopeRecord
        {
            PartitionId = consumeResult.Partition.Value,
            StreamNamespace = streamNamespace,
            StreamId = streamId,
            Payload = consumeResult.Message.Value,
        };
    }

    private async Task DeliverToPartitionHandlersAsync(
        PartitionEnvelopeRecord record,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<Func<PartitionEnvelopeRecord, Task>> handlers;
            lock (_lock)
            {
                handlers = _recordHandlers.TryGetValue(record.PartitionId, out var current)
                    ? [.. current]
                    : [];
            }

            if (handlers.Count > 0)
            {
                foreach (var handler in handlers)
                    await handler(record);

                return;
            }

            await Task.Delay(NoHandlerRetryDelay, ct);
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
            : Encoding.UTF8.GetString(bytes);
    }

    private void RemoveLifecycleHandler(Func<PartitionLifecycleEvent, Task> handler)
    {
        lock (_lock)
        {
            var next = new List<Func<PartitionLifecycleEvent, Task>>(_lifecycleHandlers);
            next.Remove(handler);
            _lifecycleHandlers = next;
        }
    }

    private void RemoveRecordHandler(int partitionId, Func<PartitionEnvelopeRecord, Task> handler)
    {
        lock (_lock)
        {
            if (!_recordHandlers.TryGetValue(partitionId, out var current))
                return;

            var handlers = current.Where(x => x != handler).ToList();
            var next = new Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>>(_recordHandlers);
            if (handlers.Count == 0)
                next.Remove(partitionId);
            else
                next[partitionId] = handlers;
            _recordHandlers = next;
        }
    }

    private sealed class CallbackSubscription : IAsyncDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public CallbackSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;

            _dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task DispatchLifecycleEventsAsync(
        IReadOnlyCollection<TopicPartition> partitions,
        PartitionLifecycleEventKind kind,
        IReadOnlyCollection<Func<PartitionLifecycleEvent, Task>> handlers)
    {
        foreach (var partition in partitions)
        {
            var lifecycleEvent = new PartitionLifecycleEvent(partition.Partition.Value, kind);
            foreach (var handler in handlers)
                await InvokeLifecycleHandlerWithRetriesAsync(lifecycleEvent, handler);
        }
    }

    private async Task InvokeLifecycleHandlerWithRetriesAsync(
        PartitionLifecycleEvent lifecycleEvent,
        Func<PartitionLifecycleEvent, Task> handler)
    {
        for (var attempt = 1; attempt <= LifecycleRetryAttempts; attempt++)
        {
            try
            {
                await handler(lifecycleEvent);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Kafka partition lifecycle handler failed for partition {PartitionId} during {LifecycleKind} on attempt {Attempt}/{MaxAttempts}.",
                    lifecycleEvent.PartitionId,
                    lifecycleEvent.Kind,
                    attempt,
                    LifecycleRetryAttempts);

                if (attempt == LifecycleRetryAttempts)
                    return;

                await Task.Delay(LifecycleRetryDelay);
            }
        }
    }
}
