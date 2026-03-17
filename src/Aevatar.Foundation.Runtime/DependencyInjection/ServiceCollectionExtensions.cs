// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions - dependency injection extensions.
// AddAevatarRuntime registers a full local actor runtime and related services.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Foundation.Abstractions.Deduplication;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Routing;
using Aevatar.Foundation.Runtime.Streaming;
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

        // Actor Runtime
        services.TryAddSingleton<IActorRuntime, LocalActorRuntime>();

        // Persistence
        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();

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
