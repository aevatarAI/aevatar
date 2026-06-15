using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

public sealed class KafkaProviderProducer :
    IHostedService,
    IAsyncDisposable
{
    private const string StreamNamespaceHeader = KafkaProviderHeaderConstants.StreamNamespace;
    private const string StreamIdHeader = KafkaProviderHeaderConstants.StreamId;

    private readonly KafkaProviderTransportOptions _transportOptions;
    private readonly KafkaQueuePartitionMapper _mapper;
    private readonly ILogger<KafkaProviderProducer> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private IProducer<Null, byte[]>? _producer;
    private bool _started;

    public KafkaProviderProducer(
        KafkaProviderTransportOptions transportOptions,
        KafkaQueuePartitionMapper mapper,
        ILoggerFactory? loggerFactory = null)
    {
        _transportOptions = transportOptions;
        _mapper = mapper;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaProviderProducer>();
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

        var metadata = admin.GetMetadata(_transportOptions.TopicName, _transportOptions.MetadataTimeout);
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
