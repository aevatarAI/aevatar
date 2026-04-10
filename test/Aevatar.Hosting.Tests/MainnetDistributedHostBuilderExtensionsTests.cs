using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
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
    public void AddMainnetDistributedOrleansHost_WhenKafkaProviderConfigured_ShouldRegisterKafkaTransport()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
            ["ActorRuntime:OrleansStreamBackend"] = "KafkaProvider",
            ["ActorRuntime:OrleansPersistenceBackend"] = "Garnet",
            ["ActorRuntime:OrleansGarnetConnectionString"] = "127.0.0.1:6379",
            ["ActorRuntime:KafkaBootstrapServers"] = "localhost:19092",
            ["ActorRuntime:KafkaTopicName"] = "mainnet-kafka-provider-events",
            ["ActorRuntime:KafkaConsumerGroup"] = "mainnet-kafka-provider-group",
            ["Orleans:QueueCount"] = "6",
            ["Orleans:QueueCacheSize"] = "512",
        });

        builder.AddAevatarDefaultHost();
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();
        var runtimeOptions = app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>();
        var transportOptions = app.Services.GetRequiredService<KafkaProviderTransportOptions>();

        runtimeOptions.QueueCount.Should().Be(6);
        runtimeOptions.QueueCacheSize.Should().Be(512);
        transportOptions.TopicPartitionCount.Should().Be(6);
        transportOptions.TopicName.Should().Be("mainnet-kafka-provider-events");
        app.Services.GetRequiredService<IQueueAdapterFactory>().Should().BeOfType<KafkaProviderQueueAdapterFactory>();
        app.Services.GetRequiredService<KafkaProviderProducer>().Should().NotBeNull();
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_EnvironmentVariables_ShouldOverrideDistributedJson()
    {
        // Simulate Distributed.json defaults via in-memory collection (loaded first).
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
            ["ActorRuntime:OrleansStreamBackend"] = "KafkaProvider",
            ["ActorRuntime:OrleansPersistenceBackend"] = "Garnet",
            ["ActorRuntime:OrleansGarnetConnectionString"] = "127.0.0.1:6379",
            ["ActorRuntime:KafkaBootstrapServers"] = "localhost:19092",
            ["ActorRuntime:KafkaTopicName"] = "topic",
            ["ActorRuntime:KafkaConsumerGroup"] = "group",
            ["Projection:Policies:Environment"] = "Production",
        });

        // Set env vars that should override the above after AddMainnetDistributedOrleansHost.
        using var prefixed = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__OrleansPersistenceBackend", "InMemory");
        using var bare = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");

        builder.AddAevatarDefaultHost();
        builder.AddMainnetDistributedOrleansHost();

        // AEVATAR_ prefixed env var should win.
        builder.Configuration["ActorRuntime:OrleansPersistenceBackend"]
            .Should().Be("InMemory", "AEVATAR_ prefixed env vars must override Distributed.json");

        // Bare env var should win.
        builder.Configuration["Projection:Policies:Environment"]
            .Should().Be("Development", "bare env vars must override Distributed.json");
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

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
