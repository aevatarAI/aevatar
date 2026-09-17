using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Configurations;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Streams;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Streaming;
using Orleans.Runtime;

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
        services.Replace(ServiceDescriptor.Singleton<IRuntimeActorStateSchemaActivationSealSupport,
            OrleansRuntimeActorStateSchemaActivationSealSupport>());
        services.AddSerializer(serializerBuilder => serializerBuilder.AddProtobufSerializer());
        services.TryAddSingleton<EventSourcingRuntimeOptions>();
        services.RemoveAll(typeof(IStateStore<>));
        services.RemoveAll(typeof(IEventSourcingSnapshotStore<>));
        services.RemoveAll(typeof(IEventSourcingBehaviorFactory<>));
        services.RemoveAll<ICommittedStatePublicationStateStore>();
        services.TryAddSingleton<IRuntimeActorStateBindingAccessor, AsyncLocalRuntimeActorStateBindingAccessor>();
        services.TryAddSingleton<AsyncLocalRuntimeActorStateSchemaContextAccessor>();
        services.TryAddSingleton<IRuntimeActorStateSchemaContextReader>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<IRuntimeActorStateSchemaContextAccessor>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<IRuntimeActorStateSchemaContextBinder>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeActorStateSchemaContextAccessor>());
        services.TryAddSingleton<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>();
        services.TryAddSingleton<IRuntimeFleetReconcileDeliveryAttestationReader>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>());
        services.TryAddSingleton<IRuntimeFleetReconcileDeliveryAttestationBinder>(sp =>
            sp.GetRequiredService<AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor>());
        services.TryAddTransient(typeof(IStateStore<>), typeof(RuntimeActorGrainStateStore<>));
        services.TryAddTransient(typeof(IEventSourcingSnapshotStore<>), typeof(RuntimeActorGrainEventSourcingSnapshotStore<>));
        services.TryAddTransient<ICommittedStatePublicationStateStore, RuntimeActorGrainCommittedStatePublicationStateStore>();
        services.TryAddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet))
        {
            services.AddGarnetEventStore(garnetOptions =>
            {
                garnetOptions.ConnectionString = options.GarnetConnectionString;
            });

            // Garnet hosts both the event store and Orleans's PubSubStore (see
            // EnsurePersistentStreamPubSubStorage). Stale rendezvous state from
            // an earlier silo wave / retired actor type can otherwise block
            // RegisterAsStreamProducer with InconsistentStateException.
            services.TryAddSingleton<IStreamPubSubMaintenance, OrleansRedisStreamPubSubMaintenance>();
        }
        else
        {
            services.TryAddSingleton<IEventStore, InMemoryEventStore>();
            services.TryAddSingleton<IEventStoreMaintenance>(sp =>
                (IEventStoreMaintenance)sp.GetRequiredService<IEventStore>());
        }

        services.TryAddSingleton<IActorDeactivationHookDispatcher, ActorDeactivationHookDispatcher>();
        services.TryAddSingleton<IRuntimeFleetCapabilityAdmissionReader,
            DenyAllRuntimeFleetCapabilityAdmissionReader>();
        services.TryAddSingleton<IRuntimeFleetCapabilityQuiescenceReader,
            DenyAllRuntimeFleetCapabilityQuiescenceReader>();
        services.TryAddSingleton<OrleansRuntimeFleetMembershipOptions>();
        services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetMembershipSnapshotSource,
            OrleansRuntimeFleetMembershipSnapshotSource>());
        services.Replace(ServiceDescriptor.Singleton<IRuntimeLocalMembershipIdentityReader,
            OrleansRuntimeLocalMembershipIdentityReader>());

        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IAgentKindVerifier, DefaultAgentKindVerifier>();
        services.TryAddSingleton(typeof(IAgentClassDefaultsProvider<>), typeof(NullAgentClassDefaultsProvider<>));
        // Replace (not TryAdd): the shared local runtime extension registers the in-memory
        // callback scheduler first, so a TryAdd here is silently a no-op and production
        // (Provider=Orleans) keeps the in-memory scheduler — durable timeouts/reminders then
        // live in process memory and are lost on every pod restart. Replace guarantees the
        // durable Orleans scheduler wins, consistent with IActorRuntime/IActorDispatchPort/IActorKindProbe.
        services.TryAddSingleton<OrleansActorRuntimeDurableCallbackScheduler>();
        services.Replace(ServiceDescriptor.Singleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<OrleansActorRuntimeDurableCallbackScheduler>()));
        services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetReconcileScheduleOwner>(sp =>
            sp.GetRequiredService<OrleansActorRuntimeDurableCallbackScheduler>()));
        services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetReconcileDeliveryVerifier>(sp =>
            sp.GetRequiredService<OrleansActorRuntimeDurableCallbackScheduler>()));
        services.Replace(ServiceDescriptor.Singleton<IActorKindProbe, OrleansActorKindProbe>());
        // Kind-token identity registry. Modules contribute their kinds in
        // their own DI extensions; the runtime guarantees the registry is
        // available here so RuntimeActorGrain resolves identities by kind.
        services.AddAevatarAgentKindRegistry(builder =>
            builder.ScanAssemblies(typeof(RuntimeFleetCapabilityAuthorityGAgent).Assembly));
        services.TryAddSingleton<IActorEventSubscriptionProvider>(sp =>
            new StreamProviderActorEventSubscriptionProvider(sp.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>()));
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

        if (IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider))
        {
            builder.AddPersistentStreams(
                options.StreamProviderName,
                (sp, _) => ResolveQueueAdapterFactory(sp),
                configurator => configurator.ConfigurePullingAgent(
                    pullingAgent => pullingAgent.Configure(
                        configured => configured.MaxEventDeliveryTime = options.MaxEventDeliveryTime)));
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
                orleansOptions.MaxEventDeliveryTime = options.MaxEventDeliveryTime;
            });
            services.TryAddEnumerable(ServiceDescriptor.Singleton<
                ILifecycleParticipant<ISiloLifecycle>,
                RuntimeFleetAuthoritySiloLifecycleParticipant>());
        });

        return builder;
    }

    private static void ValidateOptions(AevatarOrleansRuntimeOptions options)
    {
        var isInMemoryStream = IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendInMemory);
        var isKafkaProviderStream = IsStreamBackend(options, AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider);
        if (!isInMemoryStream && !isKafkaProviderStream)
            throw new InvalidOperationException($"Unsupported Orleans stream backend '{options.StreamBackend}'.");

        var isInMemoryPersistence = IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendInMemory);
        var isGarnetPersistence = IsPersistenceBackend(options, AevatarOrleansRuntimeOptions.PersistenceBackendGarnet);
        if (!isInMemoryPersistence && !isGarnetPersistence)
            throw new InvalidOperationException($"Unsupported Orleans persistence backend '{options.PersistenceBackend}'.");

        if (isKafkaProviderStream && !isGarnetPersistence)
            throw new InvalidOperationException("Kafka strict provider Orleans stream backend requires Garnet persistence for distributed stream pub/sub correctness.");

        if (isKafkaProviderStream && options.MaxEventDeliveryTime <= TimeSpan.Zero)
            throw new InvalidOperationException("Kafka strict provider max event delivery time must be positive.");

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
            builder.AddRedisGrainStorage(
                OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName,
                redisOptions =>
                {
                    redisOptions.ConfigurationOptions = StackExchange.Redis.ConfigurationOptions.Parse(options.GarnetConnectionString);
                    redisOptions.GrainStorageSerializer = new RuntimeCallbackSchedulerStateGrainStorageSerializer();
                    redisOptions.GetStorageKey = static (serviceId, grainId) =>
                        (StackExchange.Redis.RedisKey)
                        $"{OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName}/{grainId}/{serviceId}";
                });
            return;
        }

        builder.AddMemoryGrainStorage(OrleansRuntimeConstants.GrainStateStorageName);
        builder.AddMemoryGrainStorage(
            OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName,
            storageOptions => storageOptions.GrainStorageSerializer =
                new RuntimeCallbackSchedulerStateGrainStorageSerializer());
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
        builder.Configure<ReminderOptions>(reminderOptions =>
            reminderOptions.MinimumReminderPeriod =
                RuntimeCallbackSchedulerGrain.FleetReconcilePeriod);

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
