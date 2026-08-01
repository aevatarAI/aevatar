using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Confluent.Kafka;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class KafkaProviderTransportTests
{
    [Fact]
    public void KafkaQueuePartitionMapper_ShouldProvideStablePartitionQueueMapping()
    {
        var mapper = new KafkaQueuePartitionMapper("kafka-provider", 4);
        var partitionId1 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var partitionId2 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var queueId = mapper.GetQueueId(partitionId1);

        partitionId1.Should().Be(partitionId2);
        mapper.GetPartitionId(queueId).Should().Be(partitionId1);
        mapper.GetQueueForStream(StreamId.Create("aevatar.events", "actor-1")).Should().Be(queueId);
        mapper.GetAllQueues().Should().HaveCount(4);
    }

    [Fact]
    public async Task KafkaProviderBackend_ShouldRegisterProviderNativeComponents()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider,
            StreamProviderName = "kafka-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 4,
            QueueCacheSize = 256,
        });
        services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
        {
            options.BootstrapServers = "localhost:19092";
            options.TopicName = "kafka-provider-topic";
            options.ConsumerGroup = "kafka-provider-group";
            options.TopicPartitionCount = 4;
        });

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaProviderQueueAdapterFactory>();
        provider.GetRequiredService<KafkaProviderProducer>().Should().NotBeNull();
        provider.GetRequiredService<KafkaProviderTransportOptions>().TopicPartitionCount.Should().Be(4);
    }

    [Fact]
    public async Task KafkaProviderQueueAdapterFactory_ShouldCreateAdapterWithKafkaQueueMapper()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider,
            StreamProviderName = "kafka-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 4,
            QueueCacheSize = 256,
        });
        services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
        {
            options.BootstrapServers = "localhost:19092";
            options.TopicName = "kafka-provider-topic";
            options.ConsumerGroup = "kafka-provider-group";
            options.TopicPartitionCount = 4;
        });

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IQueueAdapterFactory>();
        var mapper = factory.GetStreamQueueMapper();
        var adapter = await factory.CreateAdapter();
        var streamId = StreamId.Create("aevatar.events", "actor-42");
        var queueId = mapper.GetQueueForStream(streamId);
        var receiver = adapter.CreateReceiver(queueId);

        adapter.GetType().Name.Should().Be("KafkaProviderQueueAdapter");
        receiver.GetType().Name.Should().Be("KafkaProviderQueueAdapterReceiver");
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleansKafkaProviderTransport_WhenOptionsMissing_ShouldThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
        {
            options.BootstrapServers = string.Empty;
            options.TopicName = "kafka-provider-topic";
            options.ConsumerGroup = "kafka-provider-group";
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddAevatarFoundationRuntimeOrleansKafkaProviderTransport_WhenServicesNull_ShouldThrow()
    {
        var act = () => ((IServiceCollection)null!).AddAevatarFoundationRuntimeOrleansKafkaProviderTransport();
        var actWithConfigure = () => ((IServiceCollection)null!).AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(_ => { });

        act.Should().Throw<ArgumentNullException>();
        actWithConfigure.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task KafkaProviderProducer_ShouldValidateStartPartitionsBeforeKafkaCall()
    {
        var runtimeOptions = new AevatarOrleansRuntimeOptions
        {
            StreamProviderName = "kafka-provider",
            QueueCount = 4,
        };
        var transportOptions = new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:19092",
            TopicName = "kafka-topic-validation",
            ConsumerGroup = "kafka-group-validation",
            TopicPartitionCount = 2,
        };
        var mapper = new KafkaQueuePartitionMapper(runtimeOptions.StreamProviderName, Math.Max(1, runtimeOptions.QueueCount));
        var producer = new KafkaProviderProducer(transportOptions, mapper);

        var act = () => producer.StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*QueueCount == TopicPartitionCount*");
    }

    [Fact]
    public async Task KafkaProviderProducer_ShouldValidatePayloadBeforeStart()
    {
        var runtimeOptions = new AevatarOrleansRuntimeOptions
        {
            StreamProviderName = "kafka-provider",
            QueueCount = 4,
        };
        var transportOptions = new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:19092",
            TopicName = "kafka-topic-validation",
            ConsumerGroup = "kafka-group-validation",
            TopicPartitionCount = 4,
        };
        var mapper = new KafkaQueuePartitionMapper(runtimeOptions.StreamProviderName, Math.Max(1, runtimeOptions.QueueCount));
        var producer = new KafkaProviderProducer(transportOptions, mapper);

        var emptyNamespace = () => producer.PublishAsync(string.Empty, "actor-id", [1, 2, 3]);
        var emptyStreamId = () => producer.PublishAsync("aevatar.events", "   ", [1, 2, 3]);
        var nullPayload = () => producer.PublishAsync("aevatar.events", "actor-id", null!);

        await emptyNamespace.Should().ThrowAsync<ArgumentException>();
        await emptyStreamId.Should().ThrowAsync<ArgumentException>();
        await nullPayload.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void BuildProducerConfig_ShouldCompressAndRaiseMaxMessageBytes_ByDefault()
    {
        var options = new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:19092",
            TopicName = "kafka-provider-topic",
            ConsumerGroup = "kafka-provider-group",
            TopicPartitionCount = 4,
        };

        var config = KafkaProviderProducer.BuildProducerConfig(options);

        // Large actor event envelopes (e.g. aggregated tool-call results from a /daily-style skill
        // run) must compress under the broker's ~1 MB max.message.bytes; without this the broker
        // rejects the produce ("Broker: Message size too large") and the run fails silently with a
        // generic "Sorry, I wasn't able to generate a response" reply.
        config.CompressionType.Should().Be(CompressionType.Gzip);
        config.MessageMaxBytes.Should().Be(10 * 1024 * 1024);
        config.EnableIdempotence.Should().BeTrue();
        config.Acks.Should().Be(Acks.All);
        config.BootstrapServers.Should().Be("localhost:19092");
    }

    [Fact]
    public void BuildProducerConfig_ShouldHonorConfiguredCompressionAndSize()
    {
        var options = new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:19092",
            TopicName = "kafka-provider-topic",
            ConsumerGroup = "kafka-provider-group",
            TopicPartitionCount = 4,
            ProducerCompressionType = CompressionType.Zstd,
            ProducerMaxMessageBytes = 4 * 1024 * 1024,
        };

        var config = KafkaProviderProducer.BuildProducerConfig(options);

        config.CompressionType.Should().Be(CompressionType.Zstd);
        config.MessageMaxBytes.Should().Be(4 * 1024 * 1024);
    }

    [Fact]
    public void ClassifyPolledRecord_ShouldDistinguishDeliverForeignAndMalformedRecords()
    {
        var receiver = CreateReceiver();
        var validEnvelope = new EventEnvelope
        {
            Id = "valid-envelope",
            Payload = Any.Pack(new StringValue { Value = "ok" }),
        };

        var deliver = receiver.ClassifyPolledRecord(CreateRecord(validEnvelope.ToByteArray()));
        var foreign = receiver.ClassifyPolledRecord(CreateRecord([], "other.events", null));
        var missingRoute = receiver.ClassifyPolledRecord(CreateRecord(validEnvelope.ToByteArray(), null, null));
        var emptyPayload = receiver.ClassifyPolledRecord(CreateRecord([]));
        var invalidProtobuf = receiver.ClassifyPolledRecord(CreateRecord([0xff, 0xff]));

        deliver.Disposition.Should().Be(KafkaPolledRecordDisposition.Deliver);
        deliver.Envelope.Should().BeEquivalentTo(validEnvelope);
        foreign.Disposition.Should().Be(KafkaPolledRecordDisposition.AcknowledgeForeignRecord);
        foreign.InvalidReason.Should().Be(KafkaInvalidRecordReason.None);
        missingRoute.Disposition.Should().Be(KafkaPolledRecordDisposition.AcknowledgeInvalidRecord);
        missingRoute.InvalidReason.Should().Be(KafkaInvalidRecordReason.MissingRoutingHeaders);
        emptyPayload.InvalidReason.Should().Be(KafkaInvalidRecordReason.EmptyPayload);
        invalidProtobuf.InvalidReason.Should().Be(KafkaInvalidRecordReason.ProtobufParseFailed);
        invalidProtobuf.ParseException.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessPolledRecord_AfterShutdown_ShouldPreserveValidRecordForRedelivery()
    {
        var receiver = CreateReceiver();
        var envelope = new EventEnvelope
        {
            Id = "shutdown-race-envelope",
            Payload = Any.Pack(new StringValue { Value = "ok" }),
        };
        var record = CreateRecord(envelope.ToByteArray());
        await receiver.Shutdown(TimeSpan.Zero);

        var shouldContinue = receiver.ProcessPolledRecord(record);

        shouldContinue.Should().BeFalse();
        receiver.ClassifyPolledRecord(record).Disposition
            .Should().Be(KafkaPolledRecordDisposition.PreserveForRedelivery);
        (await receiver.GetQueueMessagesAsync(1)).Should().BeEmpty();
    }

    private static KafkaProviderQueueAdapterReceiver CreateReceiver()
    {
        var options = new KafkaProviderTransportOptions
        {
            BootstrapServers = "localhost:19092",
            TopicName = "kafka-provider-topic",
            ConsumerGroup = "kafka-provider-group",
            TopicPartitionCount = 4,
        };
        var mapper = new KafkaQueuePartitionMapper("kafka-provider", 4);
        var producer = new KafkaProviderProducer(options, mapper);
        return new KafkaProviderQueueAdapterReceiver(
            mapper.GetQueueId(0),
            producer,
            options,
            mapper,
            "aevatar.events");
    }

    private static ConsumeResult<Ignore, byte[]> CreateRecord(
        byte[] payload,
        string? streamNamespace = "aevatar.events",
        string? streamId = "actor-42")
    {
        var headers = new Headers();
        if (streamNamespace != null)
        {
            headers.Add(
                KafkaProviderHeaderConstants.StreamNamespace,
                Encoding.UTF8.GetBytes(streamNamespace));
        }

        if (streamId != null)
        {
            headers.Add(
                KafkaProviderHeaderConstants.StreamId,
                Encoding.UTF8.GetBytes(streamId));
        }

        return new ConsumeResult<Ignore, byte[]>
        {
            Topic = "kafka-provider-topic",
            Partition = new Partition(0),
            Offset = new Offset(42),
            Message = new Message<Ignore, byte[]>
            {
                Value = payload,
                Headers = headers,
            },
        };
    }

}
