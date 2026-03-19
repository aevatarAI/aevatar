using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

public sealed class KafkaStrictProviderProducer :
    IHostedService,
    IAsyncDisposable
{
    private const string StreamNamespaceHeader = "aevatar-stream-namespace";
    private const string StreamIdHeader = "aevatar-stream-id";

    private readonly KafkaStrictProviderTransportOptions _transportOptions;
    private readonly StrictQueuePartitionMapper _mapper;
    private readonly ILogger<KafkaStrictProviderProducer> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private IProducer<Null, byte[]>? _producer;
    private bool _started;

    public KafkaStrictProviderProducer(
        KafkaStrictProviderTransportOptions transportOptions,
        AevatarOrleansRuntimeOptions runtimeOptions,
        ILoggerFactory? loggerFactory = null)
    {
        _transportOptions = transportOptions;
        _mapper = new StrictQueuePartitionMapper(
            runtimeOptions.StreamProviderName,
            Math.Max(1, runtimeOptions.QueueCount));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaStrictProviderProducer>();
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

            if (_producer != null)
            {
                _producer.Flush(ct);
                _producer.Dispose();
                _producer = null;
            }

            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
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
}
