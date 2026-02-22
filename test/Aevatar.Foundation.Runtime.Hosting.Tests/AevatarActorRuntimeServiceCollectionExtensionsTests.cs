using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using Aevatar.Foundation.Abstractions.TypeSystem;
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

        services.AddAevatarActorRuntime(configuration, options => options.Provider = "InMemory");
        using var provider = services.BuildServiceProvider();

        provider.GetService<IActorRuntime>().Should().NotBeNull();
        provider.GetRequiredService<AevatarActorRuntimeOptions>().Provider.Should().Be(AevatarActorRuntimeOptions.ProviderInMemory);
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansWithKafkaTransport_ShouldRegisterTransportSender()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:Transport"] = AevatarActorRuntimeOptions.TransportKafka,
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"] = "localhost:19092",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"] = "runtime-events",
        });

        services.AddAevatarActorRuntime(configuration);

        services.Should().Contain(x => x.ServiceType == typeof(IOrleansTransportEventSender));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AevatarActorRuntimeOptions>();
        options.Transport.Should().Be(AevatarActorRuntimeOptions.TransportKafka);
        options.KafkaBootstrapServers.Should().Be("localhost:19092");
        options.KafkaTopicName.Should().Be("runtime-events");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenOrleansTransportIsUnsupported_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:Transport"] = "RabbitMq",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported Orleans transport*");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }
}
