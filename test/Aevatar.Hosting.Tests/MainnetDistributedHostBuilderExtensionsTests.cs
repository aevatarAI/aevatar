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
        // Use env vars for values that must survive Distributed.json loading.
        // appsettings.Distributed.json is copied to the test output directory
        // by the build and would override in-memory collection values.
        using var streamBackend = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansStreamBackend", "KafkaProvider");
        using var persistence = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansPersistenceBackend", "Garnet");
        using var garnetConn = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansGarnetConnectionString", "127.0.0.1:6379");
        using var kafkaServers = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaBootstrapServers", "localhost:19092");
        using var topicName = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaTopicName", "mainnet-kafka-provider-events");
        using var consumerGroup = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaConsumerGroup", "mainnet-kafka-provider-group");
        using var queueCount = new EnvironmentVariableScope("AEVATAR_Orleans__QueueCount", "6");
        using var queueCacheSize = new EnvironmentVariableScope("AEVATAR_Orleans__QueueCacheSize", "512");

        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
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
        }, environmentName: "Distributed");

        // Set env vars that should override the above after AddMainnetDistributedOrleansHost.
        // Both stream and persistence must be InMemory together to pass validation.
        using var prefixedStream = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__OrleansStreamBackend", "InMemory");
        using var prefixedPersistence = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__OrleansPersistenceBackend", "InMemory");
        using var prefixedRuntimeEnv = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Policies__Environment", "Development");
        using var bare = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");

        builder.AddAevatarDefaultHost();
        builder.AddMainnetDistributedOrleansHost();

        // AEVATAR_ prefixed env vars should win.
        builder.Configuration["ActorRuntime:OrleansPersistenceBackend"]
            .Should().Be("InMemory", "AEVATAR_ prefixed env vars must override Distributed.json");
        builder.Configuration["ActorRuntime:OrleansStreamBackend"]
            .Should().Be("InMemory", "AEVATAR_ prefixed env vars must override Distributed.json");

        // Bare env var should win.
        builder.Configuration["Projection:Policies:Environment"]
            .Should().Be("Development", "bare env vars must override Distributed.json");
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_PersistentLocalEnvironment_ShouldNotLoadDistributedKafkaDefaults()
    {
        var builder = CreateBuilder(
            new Dictionary<string, string?>(),
            environmentName: "PersistentLocal");

        builder.AddAevatarDefaultHost();
        builder.AddMainnetDistributedOrleansHost();

        builder.Configuration["ActorRuntime:OrleansStreamBackend"]
            .Should().Be("InMemory", "PersistentLocal should keep its in-memory stream backend");
        builder.Configuration["ActorRuntime:OrleansPersistenceBackend"]
            .Should().Be("Garnet", "PersistentLocal should keep its Garnet persistence backend");
        builder.Configuration["ActorRuntime:KafkaBootstrapServers"]
            .Should().BeNull("PersistentLocal should not implicitly inherit Distributed Kafka defaults");
    }

    private static WebApplicationBuilder CreateBuilder(
        Dictionary<string, string?> values,
        string environmentName = "Development")
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = environmentName,
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
