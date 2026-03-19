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
}
