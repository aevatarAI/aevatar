using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Streams;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
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
        using var receiverCapacity = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaReceiverBufferCapacity", "96");
        using var receiverHighWatermark = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaReceiverBufferHighWatermark", "72");
        using var receiverLowWatermark = new EnvironmentVariableScope("AEVATAR_ActorRuntime__KafkaReceiverBufferLowWatermark", "36");
        using var queueCount = new EnvironmentVariableScope("AEVATAR_Orleans__QueueCount", "6");
        using var queueCacheSize = new EnvironmentVariableScope("AEVATAR_Orleans__QueueCacheSize", "65536");
        using var maxEventDeliveryTime = new EnvironmentVariableScope(
            "AEVATAR_Orleans__MaxEventDeliveryTime", "00:04:00");
        using var responseTimeout = new EnvironmentVariableScope(
            "AEVATAR_Orleans__ResponseTimeout", "00:05:00");
        using var keyringFile = TemporaryKeyringFile.Create();
        using var keyringPath = new EnvironmentVariableScope("AEVATAR_ActorRuntime__SecretStoreKeyringPath", keyringFile.Path);

        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();
        var runtimeOptions = app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>();
        var transportOptions = app.Services.GetRequiredService<KafkaProviderTransportOptions>();

        runtimeOptions.QueueCount.Should().Be(6);
        runtimeOptions.QueueCacheSize.Should().Be(AevatarOrleansRuntimeOptions.DefaultQueueCacheSize,
            "Mainnet must cap stale oversized cache overrides before a Kafka backlog is materialized");
        runtimeOptions.MaxEventDeliveryTime.Should().Be(TimeSpan.FromMinutes(4));
        app.Services.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.ResponseTimeout
            .Should().Be(TimeSpan.FromMinutes(5));
        transportOptions.TopicPartitionCount.Should().Be(6);
        transportOptions.TopicName.Should().Be("mainnet-kafka-provider-events");
        transportOptions.ReceiverBufferCapacity.Should().Be(96);
        transportOptions.ReceiverBufferHighWatermark.Should().Be(72);
        transportOptions.ReceiverBufferLowWatermark.Should().Be(36);
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
        // Both stream and persistence must be InMemory together to pass validation.
        using var prefixedStream = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__OrleansStreamBackend", "InMemory");
        using var prefixedPersistence = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__OrleansPersistenceBackend", "InMemory");
        using var prefixedRuntimeEnv = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Policies__Environment", "Development");
        using var bare = new EnvironmentVariableScope(
            "Projection__Policies__Environment", "Development");

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
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
    public void AddMainnetDistributedOrleansHost_StaleInternalTransportWithoutOptIn_ShouldUsePublicApi()
    {
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var internalOptIn = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__EnableInternalApiTransport", null);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://stale-nyxid.internal:3001",
            ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
        });

        builder.AddMainnetDistributedOrleansHost();

        builder.Configuration["Aevatar:NyxId:InternalApiBaseUrl"].Should().BeEmpty();
        var options = ResolveNyxIdOptions(builder.Configuration);
        options.InternalApiBaseUrl.Should().BeNull();
        options.EffectiveTransportBaseUrl.Should().Be("https://nyx-api.example.test");
        options.PublicTransportFallbackBaseUrl.Should().BeNull();
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_ExplicitEnvironmentOptIn_ShouldUseInternalTransport()
    {
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var internalOptIn = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__EnableInternalApiTransport", "true");
        using var internalApiBaseUrl = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__InternalApiBaseUrl", "http://nyxid.internal:3001");
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://stale-nyxid.internal:3001",
            ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
            ["Aevatar:NyxId:InternalApiFallbackTimeoutSeconds"] = "7",
        });

        builder.AddMainnetDistributedOrleansHost();

        builder.Configuration["Aevatar:NyxId:InternalApiBaseUrl"]
            .Should().Be("http://nyxid.internal:3001");
        var options = ResolveNyxIdOptions(builder.Configuration);
        options.InternalApiBaseUrl.Should().Be("http://nyxid.internal:3001");
        options.EffectiveTransportBaseUrl.Should().Be("http://nyxid.internal:3001");
        options.PublicTransportFallbackBaseUrl.Should().Be("https://nyx-api.example.test");
        options.InternalApiFallbackTimeoutSeconds.Should().Be(7);
    }

    [Theory]
    [InlineData("false", "not-an-absolute-url")]
    [InlineData("invalid", "https://user@stale-nyxid.internal")]
    public void AddMainnetDistributedOrleansHost_NonTrueEnvironmentGate_ShouldIgnoreConfiguredInternalTransport(
        string gateValue,
        string staleInternalApiBaseUrl)
    {
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var internalOptIn = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__EnableInternalApiTransport", gateValue);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:EnableInternalApiTransport"] = "true",
            ["Aevatar:NyxId:InternalApiBaseUrl"] = staleInternalApiBaseUrl,
            ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
        });

        builder.AddMainnetDistributedOrleansHost();

        builder.Configuration["Aevatar:NyxId:EnableInternalApiTransport"].Should().Be(gateValue);
        builder.Configuration["Aevatar:NyxId:InternalApiBaseUrl"].Should().BeEmpty();
        ResolveNyxIdOptions(builder.Configuration).EffectiveTransportBaseUrl
            .Should().Be("https://nyx-api.example.test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nyxid.internal")]
    [InlineData("http:/nyxid.internal")]
    [InlineData("ftp://nyxid.internal")]
    [InlineData("https://user@nyxid.internal")]
    [InlineData("https://nyxid.internal?mode=internal")]
    [InlineData("https://nyxid.internal#internal")]
    public void AddMainnetDistributedOrleansHost_EnabledWithInvalidInternalUrl_ShouldFailFast(
        string? internalApiBaseUrl)
    {
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var internalOptIn = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__EnableInternalApiTransport", "true");
        using var environmentInternalApiBaseUrl = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__InternalApiBaseUrl", null);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:InternalApiBaseUrl"] = internalApiBaseUrl,
            ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
        });

        var act = () => builder.AddMainnetDistributedOrleansHost();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Aevatar:NyxId:InternalApiBaseUrl*absolute HTTP(S) base URL*" +
                         "Aevatar:NyxId:EnableInternalApiTransport*");
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_EnabledWithSamePublicAndInternalUrl_ShouldNotConfigureFallback()
    {
        using var runtimeProvider = new EnvironmentVariableScope(
            "AEVATAR_ActorRuntime__Provider", "InMemory");
        using var internalOptIn = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__EnableInternalApiTransport", "true");
        using var internalApiBaseUrl = new EnvironmentVariableScope(
            "AEVATAR_Aevatar__NyxId__InternalApiBaseUrl", "https://nyx-api.example.test");
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
        });

        builder.AddMainnetDistributedOrleansHost();

        var options = ResolveNyxIdOptions(builder.Configuration);
        options.EffectiveTransportBaseUrl.Should().Be("https://nyx-api.example.test");
        options.PublicTransportFallbackBaseUrl.Should().BeNull();
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_GarnetClusteringMode_ShouldUseGarnetBackedMembership()
    {
        using var clusteringMode = new EnvironmentVariableScope("AEVATAR_Orleans__ClusteringMode", "Garnet");
        using var siloHost = new EnvironmentVariableScope("AEVATAR_Orleans__SiloHost", "10.255.0.7");
        using var streamBackend = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansStreamBackend", "KafkaProvider");
        using var persistence = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansPersistenceBackend", "Garnet");
        using var garnetConn = new EnvironmentVariableScope("AEVATAR_ActorRuntime__OrleansGarnetConnectionString", "127.0.0.1:6379");
        using var keyringFile = TemporaryKeyringFile.Create();
        using var keyringPath = new EnvironmentVariableScope("AEVATAR_ActorRuntime__SecretStoreKeyringPath", keyringFile.Path);

        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();

        app.Services.GetRequiredService<IMembershipTable>().GetType().Name.Should().Be("RedisMembershipTable",
            "Garnet clustering must store membership in the same Garnet instance as reminders and grain state");

        var clusterOptions = app.Services.GetRequiredService<IOptions<ClusterOptions>>().Value;
        clusterOptions.ClusterId.Should().Be("aevatar-mainnet-cluster");
        clusterOptions.ServiceId.Should().Be("aevatar-mainnet-host-api");

        var endpointOptions = app.Services.GetRequiredService<IOptions<EndpointOptions>>().Value;
        endpointOptions.AdvertisedIPAddress.Should().Be(IPAddress.Parse("10.255.0.7"));
        endpointOptions.SiloPort.Should().Be(11111);
        endpointOptions.SiloListeningEndpoint.Should().NotBeNull();
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_DistributedProfile_ShouldDefaultToGarnetClustering()
    {
        // Regression pin for the production rolling-deploy incident: the shipped
        // Distributed profile combined Localhost clustering with a shared Garnet
        // reminder table + grain state, so the old and new pod each ran as a
        // complete single-silo cluster, both fired every reminder, and ping-ponged
        // RuntimeCallbackSchedulerGrain etags (InconsistentStateException) until
        // the old pod died. The profile must keep membership in Garnet.
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();

        app.Services.GetRequiredService<IMembershipTable>().GetType().Name.Should().Be("RedisMembershipTable");

        var endpointOptions = app.Services.GetRequiredService<IOptions<EndpointOptions>>().Value;
        endpointOptions.AdvertisedIPAddress.Should().NotBeNull(
            "with no SiloHost configured the silo must advertise an interface address peers can reach");
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_DistributedProfile_ShouldBoundKafkaCacheRetention()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();

        app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>().QueueCacheSize
            .Should().Be(AevatarOrleansRuntimeOptions.DefaultQueueCacheSize);
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_StaleOversizedQueueCacheOverride_ShouldUseSafeCeiling()
    {
        using var queueCacheSize = new EnvironmentVariableScope(
            "AEVATAR_Orleans__QueueCacheSize", "32768");
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();

        app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>().QueueCacheSize
            .Should().Be(AevatarOrleansRuntimeOptions.DefaultQueueCacheSize);
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_SmallerQueueCacheOverride_ShouldPreserveLowerLimit()
    {
        using var queueCacheSize = new EnvironmentVariableScope(
            "AEVATAR_Orleans__QueueCacheSize", "2048");
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();

        app.Services.GetRequiredService<AevatarOrleansRuntimeOptions>().QueueCacheSize
            .Should().Be(2048);
    }

    [Fact]
    public void AddMainnetDistributedOrleansHost_DistributedProfile_ShouldKeepResponseTimeoutAboveDeliveryTime()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);
        builder.AddMainnetDistributedOrleansHost();

        using var app = builder.Build();
        var maxEventDeliveryTime = app.Services
            .GetRequiredService<AevatarOrleansRuntimeOptions>()
            .MaxEventDeliveryTime;
        var responseTimeout = app.Services
            .GetRequiredService<IOptions<SiloMessagingOptions>>()
            .Value.ResponseTimeout;

        responseTimeout.Should().Be(TimeSpan.FromMinutes(4));
        responseTimeout.Should().BeGreaterThan(maxEventDeliveryTime);
    }

    [Theory]
    [InlineData("00:03:00")]
    [InlineData("00:02:59")]
    public void AddMainnetDistributedOrleansHost_ResponseTimeoutNotAboveDeliveryTime_ShouldThrow(
        string configuredResponseTimeout)
    {
        using var responseTimeout = new EnvironmentVariableScope(
            "AEVATAR_Orleans__ResponseTimeout", configuredResponseTimeout);
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["ActorRuntime:Provider"] = "Orleans",
        });

        builder.AddAevatarDefaultHost(options => options.AllowLocalFileSecretsStore = false);

        var act = () => builder.AddMainnetDistributedOrleansHost();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Orleans:ResponseTimeout must be greater than Orleans:MaxEventDeliveryTime.");
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

    private static NyxIdToolOptions ResolveNyxIdOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddNyxIdApiAccess(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<NyxIdToolOptions>();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class TemporaryKeyringFile : IDisposable
    {
        private TemporaryKeyringFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryKeyringFile Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-secret-keyring-{Guid.NewGuid():N}.json");
            File.WriteAllText(
                path,
                """
                {
                  "activeKeyId": "key-1",
                  "keys": {
                    "key-1": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="
                  },
                  "fingerprintKey": "ZmVkY2JhOTg3NjU0MzIxMGZlZGNiYTk4NzY1NDMyMTA="
                }
                """,
                Encoding.UTF8);
            return new TemporaryKeyringFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
