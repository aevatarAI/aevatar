using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class AevatarActorRuntimeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsInMemory_ShouldRegisterActorRuntime()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAevatarActorRuntime(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IActorRuntime>().Should().NotBeNull();
        provider.GetService<IAgentTypeVerifier>().Should().NotBeNull();
        provider.GetService<IActorTypeProbe>().Should().NotBeNull();
        provider.GetRequiredService<AevatarActorRuntimeOptions>().Provider.Should().Be(AevatarActorRuntimeOptions.ProviderInMemory);
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsOrleans_ShouldRegisterOrleansRuntime()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IActorRuntime));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(OrleansActorRuntime));
        services.Should().Contain(x => x.ServiceType == typeof(IActorTypeProbe) && x.ImplementationType == typeof(OrleansActorTypeProbe));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsOrleans_ShouldUseDistributedForwardingRegistry()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IStreamForwardingRegistry));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(OrleansDistributedStreamForwardingRegistry));
        descriptor.ImplementationType.Should().NotBe(typeof(InMemoryStreamForwardingRegistry));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsOrleans_ShouldExposeConcreteOrleansStreamProvider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(OrleansStreamProviderAdapter));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = "Redis",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported ActorRuntime provider*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenConfigureOverridesProvider_ShouldUseOverride()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = "Redis",
        });

        services.AddAevatarActorRuntime(configuration, options => options.Provider = AevatarActorRuntimeOptions.ProviderInMemory);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IActorRuntime>().Should().NotBeNull();
        provider.GetRequiredService<AevatarActorRuntimeOptions>().Provider.Should().Be(AevatarActorRuntimeOptions.ProviderInMemory);
    }

    [Fact]
    public async Task AddAevatarActorRuntime_WhenOrleansWithKafkaProviderBackend_ShouldRegisterKafkaProviderTransport()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = AevatarActorRuntimeOptions.OrleansStreamBackendKafkaProvider,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "127.0.0.1:6379",
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansQueueCount"] = "6",
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansQueueCacheSize"] = "512",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"] = "localhost:19092",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"] = "runtime-kafka-provider-events",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"] = "runtime-kafka-provider-group",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IQueueAdapterFactory) &&
                                       x.ImplementationType == typeof(KafkaProviderQueueAdapterFactory));

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        var orleansOptions = provider.GetRequiredService<AevatarOrleansRuntimeOptions>();
        var transportOptions = provider.GetRequiredService<KafkaProviderTransportOptions>();
        options.OrleansStreamBackend.Should().Be(AevatarActorRuntimeOptions.OrleansStreamBackendKafkaProvider);
        options.OrleansQueueCount.Should().Be(6);
        options.OrleansQueueCacheSize.Should().Be(512);
        options.KafkaBootstrapServers.Should().Be("localhost:19092");
        options.KafkaTopicName.Should().Be("runtime-kafka-provider-events");
        options.KafkaConsumerGroup.Should().Be("runtime-kafka-provider-group");
        orleansOptions.QueueCount.Should().Be(6);
        orleansOptions.QueueCacheSize.Should().Be(512);
        transportOptions.TopicPartitionCount.Should().Be(6);
        provider.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaProviderQueueAdapterFactory>();
        provider.GetRequiredService<KafkaProviderProducer>().Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenEventSourcingUsesNestedSectionKeys_ShouldBindNestedValues()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableSnapshots"] = "false",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:SnapshotInterval"] = "17",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableEventCompaction"] = "false",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:RetainedEventsAfterSnapshot"] = "9",
        });

        services.AddAevatarActorRuntime(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();

        options.EventSourcingEnableSnapshots.Should().BeFalse();
        options.EventSourcingSnapshotInterval.Should().Be(17);
        options.EventSourcingEnableEventCompaction.Should().BeFalse();
        options.EventSourcingRetainedEventsAfterSnapshot.Should().Be(9);
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansStreamBackendIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = "RabbitMq",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported Orleans stream backend*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceOptionsConfigured_ShouldBindValues()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379,abortConnect=false",
        });

        services.AddAevatarActorRuntime(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.OrleansPersistenceBackend.Should().Be(AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet);
        options.OrleansGarnetConnectionString.Should().Be("garnet.local:6379,abortConnect=false");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsOrleans_ShouldReplaceOpenGenericIStateStoreWithRuntimeActorStateStore()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IStateStore<>));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(RuntimeActorGrainStateStore<>));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceBackendIsGarnet_ShouldRegisterGarnetEventStore()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379,abortConnect=false",
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IEventStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(GarnetEventStore));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceBackendIsInMemory_ShouldKeepInMemoryEventStore()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendInMemory,
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IEventStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(InMemoryEventStore));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceBackendIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = "MongoDB",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported Orleans persistence backend*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceBackendIsGarnetWithoutConnectionString_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
        });

        var act = () => services.AddAevatarActorRuntime(configuration, options =>
        {
            options.OrleansGarnetConnectionString = "   ";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }
}
