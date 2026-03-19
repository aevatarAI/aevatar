using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Configurations;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Streams;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarFoundationRuntimeOrleans(
        this IServiceCollection services,
        Action<AevatarOrleansRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AevatarOrleansRuntimeOptions();
        configure?.Invoke(options);
        ValidateOptions(options);
        services.Replace(ServiceDescriptor.Singleton(options));

        services.Replace(ServiceDescriptor.Singleton<IActorRuntime, OrleansActorRuntime>());
        services.Replace(ServiceDescriptor.Singleton<IActorDispatchPort, OrleansActorDispatchPort>());
        services.AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer());
        services.TryAddSingleton<EventSourcingRuntimeOptions>();
        services.RemoveAll(typeof(IStateStore<>));
        services.RemoveAll(typeof(IEventSourcingSnapshotStore<>));
        services.RemoveAll(typeof(IEventSourcingBehaviorFactory<>));
        services.TryAddSingleton<IRuntimeActorStateBindingAccessor, AsyncLocalRuntimeActorStateBindingAccessor>();
        services.TryAddTransient(typeof(IStateStore<>), typeof(RuntimeActorGrainStateStore<>));
        services.TryAddTransient(typeof(IEventSourcingSnapshotStore<>), typeof(RuntimeActorGrainEventSourcingSnapshotStore<>));
        services.TryAddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet))
        {
            services.AddGarnetEventStore(garnetOptions =>
            {
                garnetOptions.ConnectionString = options.GarnetConnectionString;
            });
        }
        else
        {
            services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        }

        services.TryAddSingleton<IEventStoreCompactionScheduler, DeferredEventStoreCompactionScheduler>();
        services.TryAddSingleton<IActorDeactivationHook, EventStoreCompactionDeactivationHook>();
        services.TryAddSingleton<IActorDeactivationHookDispatcher, ActorDeactivationHookDispatcher>();
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        services.TryAddSingleton<IRunManager, RunManager>();
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IAgentTypeVerifier, DefaultAgentTypeVerifier>();
        services.TryAddSingleton(typeof(IAgentClassDefaultsProvider<>), typeof(NullAgentClassDefaultsProvider<>));
        services.TryAddSingleton<IActorRuntimeCallbackScheduler, OrleansActorRuntimeDurableCallbackScheduler>();
        services.Replace(ServiceDescriptor.Singleton<IActorTypeProbe, OrleansActorTypeProbe>());
        services.AddAevatarFoundationRuntimeOrleansStreaming();

        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleans(
        this ISiloBuilder builder,
        Action<AevatarOrleansRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AevatarOrleansRuntimeOptions();
        configure?.Invoke(options);
        ValidateOptions(options);

        ConfigureGrainStateStorage(builder, options);
        ConfigureReminderService(builder, options);
        EnsurePersistentStreamPubSubStorage(builder, options);

        if (IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendKafkaStrictProvider))
        {
            builder.AddPersistentStreams(
                options.StreamProviderName,
                (sp, _) => ResolveQueueAdapterFactory(sp),
                _ => { });
        }
        else if (IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendInMemory))
        {
            builder.AddMemoryStreams(options.StreamProviderName, _ => { });
        }

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = options.StreamBackend;
                orleansOptions.StreamProviderName = options.StreamProviderName;
                orleansOptions.ActorEventNamespace = options.ActorEventNamespace;
                orleansOptions.PersistenceBackend = options.PersistenceBackend;
                orleansOptions.GarnetConnectionString = options.GarnetConnectionString;
                orleansOptions.QueueCount = options.QueueCount;
                orleansOptions.QueueCacheSize = options.QueueCacheSize;
            });
        });

        return builder;
    }

    private static void ValidateOptions(AevatarOrleansRuntimeOptions options)
    {
        var isInMemoryStream = IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendInMemory);
        var isKafkaStrictProviderStream = IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendKafkaStrictProvider);
        if (!isInMemoryStream && !isKafkaStrictProviderStream)
            throw new InvalidOperationException($"Unsupported Orleans stream backend '{options.StreamBackend}'.");

        var isInMemoryPersistence = IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendInMemory);
        var isGarnetPersistence = IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet);
        if (!isInMemoryPersistence && !isGarnetPersistence)
            throw new InvalidOperationException($"Unsupported Orleans persistence backend '{options.PersistenceBackend}'.");

        if (isKafkaStrictProviderStream && !isGarnetPersistence)
            throw new InvalidOperationException("Kafka strict provider Orleans stream backend requires Garnet persistence for distributed stream pub/sub correctness.");

        if (isGarnetPersistence && string.IsNullOrWhiteSpace(options.GarnetConnectionString))
            throw new InvalidOperationException("ActorRuntime Orleans Garnet connection string is required.");
    }

    private static void ConfigureGrainStateStorage(ISiloBuilder builder, AevatarOrleansRuntimeOptions options)
    {
        if (IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet))
        {
            builder.AddRedisGrainStorage(
                OrleansRuntimeConstants.GrainStateStorageName,
                redisOptions => redisOptions.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(options.GarnetConnectionString));
            return;
        }

        builder.AddMemoryGrainStorage(OrleansRuntimeConstants.GrainStateStorageName);
    }

    private static void EnsurePersistentStreamPubSubStorage(
        ISiloBuilder builder,
        AevatarOrleansRuntimeOptions options)
    {
        // Orleans streams need pub/sub metadata storage.
        if (IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet))
        {
            builder.AddRedisGrainStorage(
                "PubSubStore",
                redisOptions => redisOptions.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(options.GarnetConnectionString));
            return;
        }

        builder.AddMemoryGrainStorage("PubSubStore");
    }

    private static void ConfigureReminderService(ISiloBuilder builder, AevatarOrleansRuntimeOptions options)
    {
        if (IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet))
        {
            builder.UseRedisReminderService(
                redisOptions => redisOptions.ConfigurationOptions =
                    StackExchange.Redis.ConfigurationOptions.Parse(options.GarnetConnectionString));
            return;
        }

        builder.UseInMemoryReminderService();
    }

    private static bool IsStreamBackend(AevatarOrleansRuntimeOptions options, string expectedBackend)
    {
        return string.Equals(options.StreamBackend, expectedBackend, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersistenceBackend(AevatarOrleansRuntimeOptions options, string expectedBackend)
    {
        return string.Equals(options.PersistenceBackend, expectedBackend, StringComparison.OrdinalIgnoreCase);
    }

    private static IQueueAdapterFactory ResolveQueueAdapterFactory(IServiceProvider serviceProvider)
    {
        var queueAdapterFactory = serviceProvider.GetService<IQueueAdapterFactory>();
        if (queueAdapterFactory != null)
            return queueAdapterFactory;

        throw new InvalidOperationException(
            "Missing Orleans stream queue adapter factory for the selected persistent stream backend.");
    }

}
