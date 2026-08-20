using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
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
        provider.GetService<IAgentKindVerifier>().Should().NotBeNull();
        provider.GetService<IActorKindProbe>().Should().NotBeNull();
        provider.GetRequiredService<ISecretVault>().Should().BeOfType<InMemorySecretVault>();
        provider.GetRequiredService<IRuntimeSecretStore>().Should().BeOfType<InMemoryRuntimeSecretStore>();
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
        services.Should().Contain(x => x.ServiceType == typeof(IActorKindProbe) && x.ImplementationType == typeof(OrleansActorKindProbe));
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProviderIsOrleans_ShouldUseDurableCallbackScheduler()
    {
        // Regression: the shared local runtime extension registers the in-memory callback
        // scheduler first, so the Orleans extension must Replace (not TryAdd) it. Otherwise
        // production keeps the in-memory scheduler and durable timeouts/reminders are lost on
        // every pod restart.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
        });

        services.AddAevatarActorRuntime(configuration);
        services.AddSingleton(Substitute.For<IGrainFactory>());
        using var provider = services.BuildServiceProvider();

        var scheduler = provider.GetRequiredService<IActorRuntimeCallbackScheduler>();
        scheduler.Should().BeOfType<OrleansActorRuntimeDurableCallbackScheduler>();
        provider.GetRequiredService<IRuntimeFleetReconcileScheduleOwner>().Should().BeSameAs(scheduler);
        provider.GetRequiredService<IRuntimeFleetReconcileDeliveryVerifier>().Should().BeSameAs(scheduler);
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

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(OrleansDistributedStreamForwardingRegistry) &&
            x.ImplementationType == typeof(OrleansDistributedStreamForwardingRegistry));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IStreamForwardingRegistry) &&
            x.ImplementationFactory != null);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IStreamForwardingBindingAuthority) &&
            x.ImplementationFactory != null);
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
        using var keyringFile = TemporaryKeyringFile.Create();
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
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaReceiverBufferCapacity"] = "96",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaReceiverBufferHighWatermark"] = "72",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaReceiverBufferLowWatermark"] = "36",
            [$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreKeyringPath"] = keyringFile.Path,
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
        options.KafkaReceiverBufferCapacity.Should().Be(96);
        options.KafkaReceiverBufferHighWatermark.Should().Be(72);
        options.KafkaReceiverBufferLowWatermark.Should().Be(36);
        orleansOptions.QueueCount.Should().Be(6);
        orleansOptions.QueueCacheSize.Should().Be(512);
        transportOptions.TopicPartitionCount.Should().Be(6);
        transportOptions.ReceiverBufferCapacity.Should().Be(96);
        transportOptions.ReceiverBufferHighWatermark.Should().Be(72);
        transportOptions.ReceiverBufferLowWatermark.Should().Be(36);
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
    public void AddAevatarActorRuntime_ShouldWireEventSourcingOptionsWithoutActorIdPrefixRecovery()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableSnapshots"] = "false",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:SnapshotInterval"] = "23",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableEventCompaction"] = "false",
            [$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:RetainedEventsAfterSnapshot"] = "11",
        });

        services.AddAevatarActorRuntime(configuration);
        using var provider = services.BuildServiceProvider();

        // Refactor (iter56/cluster-921-runtime-recovery-actor-type-marker): old=hosting actorId prefix recovery, new=actor-type marker in factory
        var eventSourcingOptions = provider.GetRequiredService<EventSourcingRuntimeOptions>();
        eventSourcingOptions.EnableSnapshots.Should().BeFalse();
        eventSourcingOptions.SnapshotInterval.Should().Be(23);
        eventSourcingOptions.EnableEventCompaction.Should().BeFalse();
        eventSourcingOptions.RetainedEventsAfterSnapshot.Should().Be(11);
        eventSourcingOptions.RecoverFromVersionDriftOnReplay.Should().BeFalse();
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
        using var keyringFile = TemporaryKeyringFile.Create();
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379,abortConnect=false",
            [$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreKeyringPath"] = keyringFile.Path,
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
        using var keyringFile = TemporaryKeyringFile.Create();
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379,abortConnect=false",
            [$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreKeyringPath"] = keyringFile.Path,
        });

        services.AddAevatarActorRuntime(configuration);

        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(IEventStore));
        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(typeof(GarnetEventStore));
        services.LastOrDefault(x => x.ServiceType == typeof(ISecretVault))!.ImplementationType
            .Should().Be(typeof(GarnetBackedSecretVault));
        services.LastOrDefault(x => x.ServiceType == typeof(IRuntimeSecretStore))!.ImplementationType
            .Should().Be(typeof(GarnetRuntimeSecretStore));
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

    [Fact]
    public void AddAevatarActorRuntime_WhenProductionAndProviderIsInMemory_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Policies:Environment"] = "Production",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*InMemory actor runtime backends are not allowed in production*")
            .WithMessage("*Provider*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProductionAndOrleansWithInMemoryBackends_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = AevatarActorRuntimeOptions.OrleansStreamBackendInMemory,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendInMemory,
            [$"{AevatarActorRuntimeOptions.SectionName}:Policies:Environment"] = "Production",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*InMemory actor runtime backends are not allowed in production*")
            .WithMessage("*OrleansStreamBackend*")
            .WithMessage("*OrleansPersistenceBackend*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenDenyInMemoryAndProviderIsInMemory_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Policies:DenyInMemoryBackends"] = "true",
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*InMemory actor runtime backends are not allowed in production*")
            .WithMessage("*Provider*");
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenProductionAndOrleansDurableBackends_ShouldSucceed()
    {
        using var keyringFile = TemporaryKeyringFile.Create();
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Provider"] = AevatarActorRuntimeOptions.ProviderOrleans,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"] = AevatarActorRuntimeOptions.OrleansStreamBackendKafkaProvider,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"] = AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet,
            [$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"] = "garnet.local:6379",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"] = "kafka.local:9092",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"] = "events",
            [$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"] = "group",
            [$"{AevatarActorRuntimeOptions.SectionName}:Policies:Environment"] = "Production",
            [$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreKeyringPath"] = keyringFile.Path,
        });

        var act = () => services.AddAevatarActorRuntime(configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAevatarActorRuntime_WhenNonProductionEnvironment_ShouldAllowInMemory()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{AevatarActorRuntimeOptions.SectionName}:Policies:Environment"] = "Development",
        });

        services.AddAevatarActorRuntime(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AevatarActorRuntimeOptions>().Provider
            .Should().Be(AevatarActorRuntimeOptions.ProviderInMemory);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
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
                """);
            return new TemporaryKeyringFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
