using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using FluentAssertions;
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

}
