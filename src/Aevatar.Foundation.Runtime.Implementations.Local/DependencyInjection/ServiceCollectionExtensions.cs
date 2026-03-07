// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions - dependency injection extensions.
// AddAevatarRuntime registers a full local actor runtime and related services.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.Configurations;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Routing;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;

/// <summary>Service registration extension methods.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers full local actor runtime (stream + actor + persistence + deduplication).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configureStreams">Optional stream buffering configuration.</param>
    /// <returns>Service collection for fluent chaining.</returns>
    public static IServiceCollection AddAevatarRuntime(
        this IServiceCollection services,
        Action<InMemoryStreamOptions>? configureStreams = null,
        Action<EventSourcingRuntimeOptions>? configureEventSourcing = null)
    {
        // Streaming
        var streamOptions = new InMemoryStreamOptions();
        configureStreams?.Invoke(streamOptions);
        services.TryAddSingleton(streamOptions);
        services.TryAddSingleton<InMemoryStreamForwardingRegistry>();
        services.TryAddSingleton<IStreamProvider>(sp =>
            new InMemoryStreamProvider(
                sp.GetRequiredService<InMemoryStreamOptions>(),
                sp.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                sp.GetRequiredService<InMemoryStreamForwardingRegistry>()));
        services.TryAddSingleton<IStreamLifecycleManager>(sp =>
            (IStreamLifecycleManager)sp.GetRequiredService<IStreamProvider>());
        services.TryAddSingleton<IStreamForwardingRegistry>(sp =>
            sp.GetRequiredService<InMemoryStreamForwardingRegistry>());
        services.TryAddSingleton<IStreamRequestReplyClient, RuntimeStreamRequestReplyClient>();
        services.TryAddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.TryAddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());

        // Actor Runtime
        services.TryAddSingleton<IActorRuntime>(sp =>
        {
            return new LocalActorRuntime(
                sp.GetRequiredService<IStreamProvider>(),
                sp,
                sp.GetRequiredService<IStreamLifecycleManager>(),
                sp.GetService<ILogger<LocalActorRuntime>>());
        });

        // Persistence
        var eventSourcingOptions = new EventSourcingRuntimeOptions();
        configureEventSourcing?.Invoke(eventSourcingOptions);
        services.Replace(ServiceDescriptor.Singleton(eventSourcingOptions));

        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton(typeof(IEventSourcingSnapshotStore<>), typeof(InMemoryEventSourcingSnapshotStore<>));
        services.TryAddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IEventStoreCompactionScheduler, DeferredEventStoreCompactionScheduler>();
        services.TryAddSingleton<IActorDeactivationHook, EventStoreCompactionDeactivationHook>();
        services.TryAddSingleton<IActorDeactivationHookDispatcher, ActorDeactivationHookDispatcher>();
        services.TryAddSingleton<ILocalActivationIndexStore, InMemoryLocalActivationIndexStore>();

        // Deduplication
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        // Routing
        services.TryAddSingleton<IRouterHierarchyStore, InMemoryRouterStore>();

        // Context
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IActorTypeProbe, LocalActorTypeProbe>();
        services.TryAddSingleton<IAgentTypeVerifier, DefaultAgentTypeVerifier>();
        services.TryAddSingleton(typeof(IAgentClassDefaultsProvider<>), typeof(NullAgentClassDefaultsProvider<>));

        return services;
    }

    /// <summary>
    /// Replaces <see cref="IEventStore"/> with file-backed persistence.
    /// </summary>
    public static IServiceCollection AddFileEventStore(
        this IServiceCollection services,
        Action<FileEventStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new FileEventStoreOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IEventStore, FileEventStore>());
        services.Replace(ServiceDescriptor.Singleton(typeof(IEventSourcingSnapshotStore<>), typeof(FileEventSourcingSnapshotStore<>)));
        return services;
    }
}
