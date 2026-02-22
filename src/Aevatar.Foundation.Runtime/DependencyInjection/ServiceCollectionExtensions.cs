// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions - dependency injection extensions.
// AddAevatarRuntime registers a full local actor runtime and related services.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Routing;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Foundation.Runtime.TypeSystem;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.DependencyInjection;

/// <summary>Service registration extension methods.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers full local actor runtime (stream + actor + persistence + deduplication).</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configureStreams">Optional stream buffering configuration.</param>
    /// <returns>Service collection for fluent chaining.</returns>
    public static IServiceCollection AddAevatarRuntime(
        this IServiceCollection services,
        Action<InMemoryStreamOptions>? configureStreams = null)
    {
        // Streaming
        var streamOptions = new InMemoryStreamOptions();
        configureStreams?.Invoke(streamOptions);
        services.TryAddSingleton(streamOptions);
        services.TryAddSingleton<IStreamProvider>(sp =>
            new InMemoryStreamProvider(
                sp.GetRequiredService<InMemoryStreamOptions>(),
                sp.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        services.TryAddSingleton<IStreamLifecycleManager>(sp =>
            sp.GetRequiredService<IStreamProvider>() as IStreamLifecycleManager
            ?? NoopStreamLifecycleManager.Instance);

        // Actor Runtime
        services.TryAddSingleton<IActorRuntime, LocalActorRuntime>();

        // Persistence
        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();

        // Deduplication
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        // Routing
        services.TryAddSingleton<IRouterHierarchyStore, InMemoryRouterStore>();

        // Context
        services.TryAddSingleton<IRunManager, RunManager>();
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<ICorrelationLinkPolicy, DefaultCorrelationLinkPolicy>();
        services.TryAddSingleton<IEnvelopePropagationPolicy, DefaultEnvelopePropagationPolicy>();
        services.TryAddSingleton<IActorTypeProbe, LocalActorTypeProbe>();
        services.TryAddSingleton<IAgentTypeVerifier, DefaultAgentTypeVerifier>();

        return services;
    }

    private sealed class NoopStreamLifecycleManager : IStreamLifecycleManager
    {
        public static readonly IStreamLifecycleManager Instance = new NoopStreamLifecycleManager();

        public void RemoveStream(string actorId)
        {
            _ = actorId;
        }
    }
}
