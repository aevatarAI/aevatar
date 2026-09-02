using System.Diagnostics.Metrics;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class KafkaProviderTransportTests
{
    [Fact]
    public void KafkaStatisticsPayload_DrivesTransportLagWithoutProjectionState()
    {
        const string statistics = """
                                  {
                                    "topics": {
                                      "events-alpha": {
                                        "partitions": {
                                          "2": { "consumer_lag": 37 }
                                        }
                                      }
                                    }
                                  }
                                  """;

        KafkaTransportMetrics.TryReadConsumerLag(statistics, "events-alpha", 2, out var lag)
            .Should().BeTrue();
        lag.Should().Be(37);
        KafkaTransportMetrics.TryReadConsumerLag(statistics, "events-alpha", 3, out _)
            .Should().BeFalse("missing provider statistics must be unavailable, not zero");
    }

    [Fact]
    public void KafkaTransportMetrics_ShouldExposeReceiverBackpressureWithLowCardinalityLabelsOnly()
    {
        var measurements = new List<(string Instrument, double Value, string[] TagKeys)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == KafkaTransportMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.Start();

        KafkaTransportMetrics.ObserveStatistics(
            """{"topics":{"events-alpha":{"partitions":{"1":{"consumer_lag":9}}}}}""",
            "kafka-provider",
            "events-alpha",
            1).Should().BeTrue();
        KafkaTransportMetrics.RecordReceiverBufferDepth("kafka-provider", "events-alpha", 1, 4);
        KafkaTransportMetrics.RecordReceiverBufferCapacity("kafka-provider", "events-alpha", 1, 16);
        KafkaTransportMetrics.RecordReceiverPausedPartitionCount("kafka-provider", "events-alpha", 1, 1);
        KafkaTransportMetrics.RecordReceiverPauseResume(
            "kafka-provider", "events-alpha", 1, KafkaTransportMetrics.PauseOperation);
        KafkaTransportMetrics.RecordReceiverPauseDuration(
            "kafka-provider", "events-alpha", 1, TimeSpan.FromMilliseconds(25));
        KafkaTransportMetrics.RecordReceiverBufferSaturation("kafka-provider", "events-alpha", 1);
        KafkaTransportMetrics.RecordReceiverConsumeError("kafka-provider", "events-alpha", 1);

        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.consumer_group.lag" && measurement.Value == 9);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.buffer_depth" && measurement.Value == 4);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.buffer_capacity" && measurement.Value == 16);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.paused_partitions" && measurement.Value == 1);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.pause_resume" && measurement.Value == 1);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.pause_duration" && measurement.Value == 25);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.buffer_saturations" && measurement.Value == 1);
        measurements.Should().Contain(measurement =>
            measurement.Instrument == "aevatar.kafka.receiver.consume_errors" && measurement.Value == 1);
        measurements.SelectMany(measurement => measurement.TagKeys).Should().OnlyContain(key =>
            key == KafkaTransportMetrics.ProviderTag ||
            key == KafkaTransportMetrics.TopicTag ||
            key == KafkaTransportMetrics.PartitionTag ||
            key == KafkaTransportMetrics.OperationTag);
    }

    [Fact]
    public void BuildConsumerConfig_DisablesStatisticsWithoutFabricatingLag()
    {
        var options = new KafkaProviderTransportOptions
        {
            StatisticsInterval = TimeSpan.Zero,
        };

        var config = KafkaProviderQueueAdapterReceiver.BuildConsumerConfig(options);

        config.StatisticsIntervalMs.Should().Be(0);
    }

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
            options.ReceiverBufferCapacity = 64;
            options.ReceiverBufferHighWatermark = 48;
            options.ReceiverBufferLowWatermark = 24;
        });

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaProviderQueueAdapterFactory>();
        provider.GetRequiredService<KafkaProviderProducer>().Should().NotBeNull();
        var transportOptions = provider.GetRequiredService<KafkaProviderTransportOptions>();
        transportOptions.TopicPartitionCount.Should().Be(4);
        transportOptions.ReceiverBufferCapacity.Should().Be(64);
        transportOptions.ReceiverBufferHighWatermark.Should().Be(48);
        transportOptions.ReceiverBufferLowWatermark.Should().Be(24);
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
    public void AddAevatarFoundationRuntimeOrleansKafkaProviderTransport_WhenWatermarksInvalid_ShouldFailRegistration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
        {
            options.ReceiverBufferCapacity = 32;
            options.ReceiverBufferHighWatermark = 24;
            options.ReceiverBufferLowWatermark = 24;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*0 < ReceiverBufferLowWatermark < ReceiverBufferHighWatermark <= ReceiverBufferCapacity*");
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

}
