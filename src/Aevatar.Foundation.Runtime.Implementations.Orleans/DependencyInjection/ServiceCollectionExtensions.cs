using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.DependencyInjection;
using Orleans.Hosting;
using Orleans.Streams;

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
        services.Replace(ServiceDescriptor.Singleton(options));

        services.Replace(ServiceDescriptor.Singleton<IActorRuntime, OrleansActorRuntime>());

        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        services.TryAddSingleton<IRunManager, RunManager>();
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IAgentTypeVerifier, DefaultAgentTypeVerifier>();
        services.Replace(ServiceDescriptor.Singleton<IActorTypeProbe, OrleansActorTypeProbe>());
        services.AddAevatarFoundationRuntimeOrleansStreaming();

        var isInMemory = string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendInMemory, StringComparison.OrdinalIgnoreCase);
        var isMassTransitAdapter = string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase);
        if (!isInMemory && !isMassTransitAdapter)
            throw new InvalidOperationException($"Unsupported Orleans stream backend '{options.StreamBackend}'.");

        return services;
    }

    public static ISiloBuilder AddAevatarFoundationRuntimeOrleans(
        this ISiloBuilder builder,
        Action<AevatarOrleansRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AevatarOrleansRuntimeOptions();
        configure?.Invoke(options);

        builder.AddMemoryGrainStorage(OrleansRuntimeConstants.GrainStateStorageName);

        if (string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
        {
            EnsurePersistentStreamPubSubStorage(builder);
            builder.AddPersistentStreams(
                options.StreamProviderName,
                (sp, _) => ResolveQueueAdapterFactory(sp),
                _ => { });
        }
        else if (string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
        {
            builder.AddMemoryStreams(options.StreamProviderName, _ => { });
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Orleans stream backend '{options.StreamBackend}'.");
        }

        builder.ConfigureServices(services =>
        {
            services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = options.StreamBackend;
                orleansOptions.StreamProviderName = options.StreamProviderName;
                orleansOptions.ActorEventNamespace = options.ActorEventNamespace;
                orleansOptions.QueueCount = options.QueueCount;
                orleansOptions.QueueCacheSize = options.QueueCacheSize;
            });
        });

        return builder;
    }

    private static void EnsurePersistentStreamPubSubStorage(ISiloBuilder builder)
    {
        // Orleans persistent streams need pub/sub metadata storage.
        builder.AddMemoryGrainStorage("PubSubStore");
    }

    private static IQueueAdapterFactory ResolveQueueAdapterFactory(IServiceProvider serviceProvider)
    {
        var queueAdapterFactory = serviceProvider.GetService<IQueueAdapterFactory>();
        if (queueAdapterFactory != null)
            return queueAdapterFactory;

        throw new InvalidOperationException(
            "Missing Orleans stream queue adapter factory for MassTransitAdapter backend. " +
            "Register it via AddAevatarFoundationRuntimeOrleansMassTransitAdapter().");
    }

}
