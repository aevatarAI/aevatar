using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.KafkaAdapter;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using Orleans.Hosting;

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
        services.Replace(ServiceDescriptor.Singleton<IStreamForwardingRegistry, OrleansDistributedStreamForwardingRegistry>());
        services.AddAevatarOrleansStreamProviderAdapter();

        if (string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendKafkaAdapter, StringComparison.OrdinalIgnoreCase))
            services.TryAddSingleton<OrleansKafkaQueueAdapterFactory>();
        else if (!string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(options.StreamBackend, AevatarOrleansRuntimeOptions.StreamBackendKafkaAdapter, StringComparison.OrdinalIgnoreCase))
        {
            builder.AddPersistentStreams(
                options.StreamProviderName,
                (sp, _) => sp.GetRequiredService<OrleansKafkaQueueAdapterFactory>(),
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

}
