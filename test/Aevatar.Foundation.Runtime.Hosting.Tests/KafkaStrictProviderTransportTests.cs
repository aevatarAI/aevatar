using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class KafkaStrictProviderTransportTests
{
    [Fact]
    public void StrictQueuePartitionMapper_ShouldProvideStablePartitionQueueMapping()
    {
        var mapper = new StrictQueuePartitionMapper("strict-provider", 4);
        var partitionId1 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var partitionId2 = mapper.GetPartitionId("aevatar.events", "actor-1");
        var queueId = mapper.GetQueueId(partitionId1);

        partitionId1.Should().Be(partitionId2);
        mapper.GetPartitionId(queueId).Should().Be(partitionId1);
        mapper.GetQueueForStream(StreamId.Create("aevatar.events", "actor-1")).Should().Be(queueId);
        mapper.GetAllQueues().Should().HaveCount(4);
    }

    [Fact]
    public async Task KafkaStrictProviderBackend_ShouldRegisterProviderNativeComponents()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaStrictProvider,
            StreamProviderName = "strict-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 4,
            QueueCacheSize = 256,
        });
        services.AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(options =>
        {
            options.BootstrapServers = "localhost:19092";
            options.TopicName = "strict-provider-topic";
            options.ConsumerGroup = "strict-provider-group";
            options.TopicPartitionCount = 4;
        });

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaStrictProviderQueueAdapterFactory>();
        provider.GetRequiredService<KafkaStrictProviderProducer>().Should().NotBeNull();
        provider.GetRequiredService<KafkaStrictProviderTransportOptions>().TopicPartitionCount.Should().Be(4);
    }

    [Fact]
    public async Task KafkaStrictProviderQueueAdapterFactory_ShouldCreateAdapterWithStrictMapper()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AevatarOrleansRuntimeOptions
        {
            StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaStrictProvider,
            StreamProviderName = "strict-provider",
            ActorEventNamespace = "aevatar.events",
            QueueCount = 4,
            QueueCacheSize = 256,
        });
        services.AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(options =>
        {
            options.BootstrapServers = "localhost:19092";
            options.TopicName = "strict-provider-topic";
            options.ConsumerGroup = "strict-provider-group";
            options.TopicPartitionCount = 4;
        });

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IQueueAdapterFactory>();
        var mapper = factory.GetStreamQueueMapper();
        var adapter = await factory.CreateAdapter();
        var streamId = StreamId.Create("aevatar.events", "actor-42");
        var queueId = mapper.GetQueueForStream(streamId);
        var receiver = adapter.CreateReceiver(queueId);

        adapter.GetType().Name.Should().Be("KafkaStrictProviderQueueAdapter");
        receiver.GetType().Name.Should().Be("KafkaStrictProviderQueueAdapterReceiver");
    }
}
