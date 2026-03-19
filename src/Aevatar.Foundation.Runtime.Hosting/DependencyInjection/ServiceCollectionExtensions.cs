using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider.DependencyInjection;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Aevatar.Foundation.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private const string EventSourcingSection = "EventSourcing";

    public static IServiceCollection AddAevatarActorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarActorRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadRuntimeOptionsFromConfiguration(configuration);
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
        {
            return AddInMemoryRuntime(services, options);
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderMassTransit, StringComparison.OrdinalIgnoreCase))
        {
            return AddMassTransitRuntime(services, options);
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
        {
            return AddOrleansRuntime(services, options);
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
    }

    private static AevatarActorRuntimeOptions ReadRuntimeOptionsFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(AevatarActorRuntimeOptions.SectionName);
        var options = new AevatarActorRuntimeOptions();
        section.Bind(options);

        var eventSourcingSection = section.GetSection(EventSourcingSection);
        options.EventSourcingEnableSnapshots = ReadBool(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingEnableSnapshots),
            options.EventSourcingEnableSnapshots);
        ReadInt(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingSnapshotInterval),
            value => options.EventSourcingSnapshotInterval = value,
            "snapshot interval");
        options.EventSourcingEnableEventCompaction = ReadBool(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingEnableEventCompaction),
            options.EventSourcingEnableEventCompaction);
        ReadInt(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingRetainedEventsAfterSnapshot),
            value => options.EventSourcingRetainedEventsAfterSnapshot = value,
            "retained events after snapshot");

        return options;
    }

    private static bool ReadBool(
        IConfigurationSection section,
        string key,
        bool defaultValue)
    {
        var nestedKey = ResolveNestedEventSourcingKey(key);
        return section.GetValue(nestedKey, section.GetValue(key, defaultValue));
    }

    private static void ReadInt(
        IConfigurationSection section,
        string key,
        Action<int> setValue,
        string settingName)
    {
        var value = section[ResolveNestedEventSourcingKey(key)] ?? section[key];
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!int.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {settingName} value '{value}'.");
        }

        setValue(parsed);
    }

    private static string ResolveNestedEventSourcingKey(string key)
    {
        return key.StartsWith(EventSourcingSection, StringComparison.Ordinal)
            ? key[EventSourcingSection.Length..]
            : key;
    }

    private static IServiceCollection AddInMemoryRuntime(IServiceCollection services, AevatarActorRuntimeOptions options)
    {
        AddAevatarRuntimeWithEventSourcingOptions(services, options);
        return services;
    }

    private static IServiceCollection AddMassTransitRuntime(IServiceCollection services, AevatarActorRuntimeOptions options)
    {
        AddAevatarRuntimeWithEventSourcingOptions(services, options);
        ConfigureMassTransitTransport(services, options);
        services.AddAevatarMassTransitStreamProvider();
        return services;
    }

    private static IServiceCollection AddOrleansRuntime(IServiceCollection services, AevatarActorRuntimeOptions options)
    {
        AddAevatarRuntimeWithEventSourcingOptions(services, options);
        services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
        {
            orleansOptions.StreamBackend = options.OrleansStreamBackend;
            orleansOptions.StreamProviderName = options.OrleansStreamProviderName;
            orleansOptions.ActorEventNamespace = options.OrleansActorEventNamespace;
            orleansOptions.PersistenceBackend = options.OrleansPersistenceBackend;
            orleansOptions.GarnetConnectionString = options.OrleansGarnetConnectionString;
            orleansOptions.QueueCount = options.OrleansQueueCount;
            orleansOptions.QueueCacheSize = options.OrleansQueueCacheSize;
        });

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
            return services;

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
        {
            ConfigureMassTransitTransport(services, options);
            services.AddAevatarMassTransitStreamProvider(streamOptions =>
            {
                streamOptions.StreamNamespace = options.OrleansActorEventNamespace;
            });
            services.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();

            return services;
        }

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendKafkaStrictProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(transportOptions =>
            {
                transportOptions.BootstrapServers = options.MassTransitKafkaBootstrapServers;
                transportOptions.TopicName = options.MassTransitKafkaTopicName;
                transportOptions.ConsumerGroup = options.MassTransitKafkaConsumerGroup;
                transportOptions.TopicPartitionCount = options.OrleansQueueCount;
            });
            return services;
        }

        if (!string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported Orleans stream backend '{options.OrleansStreamBackend}'.");
        }
        return services;
    }

    private static void AddAevatarRuntimeWithEventSourcingOptions(
        IServiceCollection services,
        AevatarActorRuntimeOptions options)
    {
        services.AddAevatarRuntime(configureEventSourcing: eventSourcingOptions =>
        {
            eventSourcingOptions.EnableSnapshots = options.EventSourcingEnableSnapshots;
            eventSourcingOptions.SnapshotInterval = options.EventSourcingSnapshotInterval;
            eventSourcingOptions.EnableEventCompaction = options.EventSourcingEnableEventCompaction;
            eventSourcingOptions.RetainedEventsAfterSnapshot = options.EventSourcingRetainedEventsAfterSnapshot;
        });
    }

    private static void ConfigureMassTransitTransport(
        IServiceCollection services,
        AevatarActorRuntimeOptions options)
    {
        if (!string.Equals(
                options.MassTransitTransportBackend,
                AevatarActorRuntimeOptions.MassTransitTransportBackendKafka,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported MassTransit transport backend '{options.MassTransitTransportBackend}'.");
        }

        services.AddAevatarFoundationRuntimeMassTransitKafkaTransport(transportOptions =>
        {
            transportOptions.BootstrapServers = options.MassTransitKafkaBootstrapServers;
            transportOptions.TopicName = options.MassTransitKafkaTopicName;
            transportOptions.ConsumerGroup = options.MassTransitKafkaConsumerGroup;
            transportOptions.TopicPartitionCount = options.MassTransitKafkaTopicPartitionCount;
        });
    }
}
