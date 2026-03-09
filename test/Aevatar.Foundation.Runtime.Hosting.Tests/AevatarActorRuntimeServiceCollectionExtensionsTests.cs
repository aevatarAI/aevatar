using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public void AddAevatarActorRuntime_WhenOrleansWithMassTransitAdapterBackend_ShouldRegisterMassTransitTransportAndStreamAdapter()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter,
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitTransportBackend"] = AevatarActorRuntimeOptions.MassTransitTransportBackendKafka,
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaBootstrapServers"] = "localhost:19092",
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaTopicName"] = "runtime-events",
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaConsumerGroup"] = "runtime-group",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IMassTransitEnvelopeTransport));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider) &&
                                       x.ImplementationType == typeof(OrleansStreamProviderAdapter));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.OrleansStreamBackend.Should().Be(AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter);
        options.MassTransitTransportBackend.Should().Be(AevatarActorRuntimeOptions.MassTransitTransportBackendKafka);
        options.MassTransitKafkaBootstrapServers.Should().Be("localhost:19092");
        options.MassTransitKafkaTopicName.Should().Be("runtime-events");
        options.MassTransitKafkaConsumerGroup.Should().Be("runtime-group");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsMassTransit_ShouldUseDirectMassTransitStreamProvider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderMassTransit,
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitTransportBackend"] = AevatarActorRuntimeOptions.MassTransitTransportBackendKafka,
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaBootstrapServers"] = "localhost:29092",
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaTopicName"] = "direct-events",
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaConsumerGroup"] = "direct-runtime-group",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IMassTransitEnvelopeTransport));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider) &&
                                       x.ImplementationType == typeof(MassTransitStreamProvider));

        var actorRuntimeDescriptor = services.LastOrDefault(x => x.ServiceType == typeof(IActorRuntime));
        actorRuntimeDescriptor.Should().NotBeNull();
        actorRuntimeDescriptor!.ImplementationType.Should().NotBe(typeof(OrleansActorRuntime));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.Provider.Should().Be(AevatarActorRuntimeOptions.ProviderMassTransit);
        options.MassTransitTransportBackend.Should().Be(AevatarActorRuntimeOptions.MassTransitTransportBackendKafka);
        options.MassTransitKafkaBootstrapServers.Should().Be("localhost:29092");
        options.MassTransitKafkaTopicName.Should().Be("direct-events");
        options.MassTransitKafkaConsumerGroup.Should().Be("direct-runtime-group");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenMassTransitTransportBackendIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderMassTransit,
            [$"{AevatarActorRuntimeOptions.SectionName}:MassTransitTransportBackend"] = "RabbitMq",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported MassTransit transport backend*");
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
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetEventStoreKeyPrefix"] = "aevatar:eventstore",
        });

        services.AddAevatarActorRuntime(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.OrleansPersistenceBackend.Should().Be(AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet);
        options.OrleansGarnetConnectionString.Should().Be("garnet.local:6379,abortConnect=false");
        options.OrleansGarnetEventStoreKeyPrefix.Should().Be("aevatar:eventstore");
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
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetEventStoreKeyPrefix"] = "aevatar:eventstore"
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
            options.OrleansGarnetEventStoreKeyPrefix = "aevatar:eventstore";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet connection string is required*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansPersistenceBackendIsGarnetWithoutKeyPrefix_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379,abortConnect=false",
        });

        var act = () => services.AddAevatarActorRuntime(configuration, options =>
        {
            options.OrleansGarnetEventStoreKeyPrefix = "   ";
        });

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Garnet EventStore key prefix is required*");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }
}
