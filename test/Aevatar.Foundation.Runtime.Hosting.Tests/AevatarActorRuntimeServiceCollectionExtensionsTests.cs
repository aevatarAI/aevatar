using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
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
    public void AddAevatarActorRuntime_WhenOrleansWithKafkaAdapterBackend_ShouldRegisterKafkaTransportAndStreamAdapter()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = AevatarActorRuntimeOptions.OrleansStreamBackendKafkaAdapter,
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"] = "localhost:19092",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"] = "runtime-events",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"] = "runtime-group",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IKafkaEnvelopeTransport));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider) &&
                                       x.ImplementationType == typeof(OrleansStreamProviderAdapter));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.OrleansStreamBackend.Should().Be(AevatarActorRuntimeOptions.OrleansStreamBackendKafkaAdapter);
        options.KafkaBootstrapServers.Should().Be("localhost:19092");
        options.KafkaTopicName.Should().Be("runtime-events");
        options.KafkaConsumerGroup.Should().Be("runtime-group");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsMassTransitKafka_ShouldUseDirectKafkaStreamProvider()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderMassTransitKafka,
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"] = "localhost:29092",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"] = "direct-events",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"] = "direct-runtime-group",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IKafkaEnvelopeTransport));
        services.Should().Contain(x => x.ServiceType == typeof(Aevatar.Foundation.Abstractions.IStreamProvider) &&
                                       x.ImplementationType == typeof(MassTransitKafkaStreamProvider));

        var actorRuntimeDescriptor = services.LastOrDefault(x => x.ServiceType == typeof(IActorRuntime));
        actorRuntimeDescriptor.Should().NotBeNull();
        actorRuntimeDescriptor!.ImplementationType.Should().NotBe(typeof(OrleansActorRuntime));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.Provider.Should().Be(AevatarActorRuntimeOptions.ProviderMassTransitKafka);
        options.KafkaBootstrapServers.Should().Be("localhost:29092");
        options.KafkaTopicName.Should().Be("direct-events");
        options.KafkaConsumerGroup.Should().Be("direct-runtime-group");
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

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }
}
