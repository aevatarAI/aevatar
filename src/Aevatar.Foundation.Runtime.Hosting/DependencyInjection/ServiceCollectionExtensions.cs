using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarActorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarActorRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AevatarActorRuntimeOptions();
        var configuredProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            options.Provider = configuredProvider;
        var configuredMassTransitTransportBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitTransportBackend"];
        if (!string.IsNullOrWhiteSpace(configuredMassTransitTransportBackend))
            options.MassTransitTransportBackend = configuredMassTransitTransportBackend;
        var configuredMassTransitKafkaBootstrap = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaBootstrapServers"];
        if (!string.IsNullOrWhiteSpace(configuredMassTransitKafkaBootstrap))
            options.MassTransitKafkaBootstrapServers = configuredMassTransitKafkaBootstrap;
        var configuredMassTransitKafkaTopic = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaTopicName"];
        if (!string.IsNullOrWhiteSpace(configuredMassTransitKafkaTopic))
            options.MassTransitKafkaTopicName = configuredMassTransitKafkaTopic;
        var configuredMassTransitKafkaConsumerGroup = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaConsumerGroup"];
        if (!string.IsNullOrWhiteSpace(configuredMassTransitKafkaConsumerGroup))
            options.MassTransitKafkaConsumerGroup = configuredMassTransitKafkaConsumerGroup;
        var configuredOrleansStreamBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansStreamBackend))
            options.OrleansStreamBackend = configuredOrleansStreamBackend;
        var configuredOrleansStreamProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamProviderName"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansStreamProvider))
            options.OrleansStreamProviderName = configuredOrleansStreamProvider;
        var configuredOrleansActorNamespace = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansActorEventNamespace"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansActorNamespace))
            options.OrleansActorEventNamespace = configuredOrleansActorNamespace;
        var configuredOrleansPersistenceBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansPersistenceBackend))
            options.OrleansPersistenceBackend = configuredOrleansPersistenceBackend;
        var configuredOrleansGarnetConnectionString = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"];
        if (!string.IsNullOrWhiteSpace(configuredOrleansGarnetConnectionString))
            options.OrleansGarnetConnectionString = configuredOrleansGarnetConnectionString;
        var configuredEventSourcingEnableSnapshots = configuration[$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableSnapshots"];
        if (bool.TryParse(configuredEventSourcingEnableSnapshots, out var eventSourcingEnableSnapshots))
            options.EventSourcingEnableSnapshots = eventSourcingEnableSnapshots;
        var configuredEventSourcingSnapshotInterval = configuration[$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:SnapshotInterval"];
        if (int.TryParse(configuredEventSourcingSnapshotInterval, out var eventSourcingSnapshotInterval))
            options.EventSourcingSnapshotInterval = eventSourcingSnapshotInterval;
        var configuredEventSourcingEnableCompaction = configuration[$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:EnableEventCompaction"];
        if (bool.TryParse(configuredEventSourcingEnableCompaction, out var eventSourcingEnableCompaction))
            options.EventSourcingEnableEventCompaction = eventSourcingEnableCompaction;
        var configuredEventSourcingRetainedEvents = configuration[$"{AevatarActorRuntimeOptions.SectionName}:EventSourcing:RetainedEventsAfterSnapshot"];
        if (int.TryParse(configuredEventSourcingRetainedEvents, out var eventSourcingRetainedEvents))
            options.EventSourcingRetainedEventsAfterSnapshot = eventSourcingRetainedEvents;
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
        {
            AddAevatarRuntimeWithEventSourcingOptions(services, options);
            return services;
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderMassTransit, StringComparison.OrdinalIgnoreCase))
        {
            AddAevatarRuntimeWithEventSourcingOptions(services, options);
            ConfigureMassTransitTransport(services, options);
            services.AddAevatarMassTransitStreamProvider();
            return services;
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
        {
            AddAevatarRuntimeWithEventSourcingOptions(services, options);
            services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = options.OrleansStreamBackend;
                orleansOptions.StreamProviderName = options.OrleansStreamProviderName;
                orleansOptions.ActorEventNamespace = options.OrleansActorEventNamespace;
                orleansOptions.PersistenceBackend = options.OrleansPersistenceBackend;
                orleansOptions.GarnetConnectionString = options.OrleansGarnetConnectionString;
            });

            if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
                return services;

            if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
            {
                ConfigureMassTransitTransport(services, options);
                services.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
                return services;
            }

            throw new InvalidOperationException(
                $"Unsupported Orleans stream backend '{options.OrleansStreamBackend}'.");
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
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
        });
    }
}
