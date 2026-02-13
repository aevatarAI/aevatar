// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions - dependency injection extensions.
// AddAevatarRuntime registers a full local actor runtime and related services.
// ─────────────────────────────────────────────────────────────

using Aevatar.Actor;
using Aevatar.Context;
using Aevatar.Deduplication;
using Aevatar.Persistence;
using Aevatar.Routing;
using Aevatar.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.DependencyInjection;

/// <summary>Service registration extension methods.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers full local actor runtime (stream + actor + persistence + deduplication).</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for fluent chaining.</returns>
    public static IServiceCollection AddAevatarRuntime(this IServiceCollection services)
    {
        // Streaming
        services.TryAddSingleton<IStreamProvider, InMemoryStreamProvider>();

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

        return services;
    }
}
