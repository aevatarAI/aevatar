using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Streams;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetDistributedHostBuilderExtensionsTests
{
    [Fact]
    public void AddMainnetDistributedOrleansHost_WhenStrictBackendConfigured_ShouldRegisterStrictKafkaTransport()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
            ["ActorRuntime:OrleansStreamBackend"] = "KafkaStrictProvider",
            ["ActorRuntime:OrleansPersistenceBackend"] = "Garnet",
            ["ActorRuntime:OrleansGarnetConnectionString"] = "127.0.0.1:6379",
            ["ActorRuntime:MassTransitKafkaBootstrapServers"] = "localhost:19092",
            ["ActorRuntime:MassTransitKafkaTopicName"] = "mainnet-strict-events",
            ["ActorRuntime:MassTransitKafkaConsumerGroup"] = "mainnet-strict-group",
            ["ActorRuntime:MassTransitKafkaTopicPartitionCount"] = "99",
            ["Orleans:QueueCount"] = "6",
            ["Orleans:QueueCacheSize"] = "512",
        });

        builder.AddAevatarDefaultHost();
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();
        var runtimeOptions = app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>();
        var transportOptions = app.Services.GetRequiredService<KafkaStrictProviderTransportOptions>();

        runtimeOptions.QueueCount.Should().Be(6);
        runtimeOptions.QueueCacheSize.Should().Be(512);
        transportOptions.TopicPartitionCount.Should().Be(6);
        transportOptions.TopicName.Should().Be("mainnet-strict-events");
        app.Services.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaStrictProviderQueueAdapterFactory>();
        app.Services.GetRequiredService<IKafkaStrictProviderEnvelopeTransport>().GetType().Name.Should().Be("KafkaStrictProviderEnvelopeTransport");
    }

    private static WebApplicationBuilder CreateBuilder(Dictionary<string, string?> values)
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }
}
