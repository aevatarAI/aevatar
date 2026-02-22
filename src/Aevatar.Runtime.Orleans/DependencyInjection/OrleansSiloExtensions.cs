// ─────────────────────────────────────────────────────────────
// OrleansSiloExtensions - Silo-side DI registration.
// Registers all services required by GAgentGrain, aligned with
// LocalRuntime's ServiceCollectionExtensions.AddAevatarRuntime().
// ─────────────────────────────────────────────────────────────

using Aevatar.Context;
using Aevatar.Deduplication;
using Aevatar.Orleans.Consumers;
using Aevatar.Orleans.Streaming;
using Aevatar.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace Aevatar.Orleans.DependencyInjection;

/// <summary>Silo-side DI extensions for Orleans runtime.</summary>
public static class OrleansSiloExtensions
{
    /// <summary>
    /// Registers all Silo-side services for the Orleans agent runtime.
    /// Requires MassTransit (IBus, ISendEndpointProvider) to be configured
    /// separately in the host's MassTransit setup.
    /// </summary>
    public static ISiloBuilder AddAevatarOrleansSilo(this ISiloBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // ── MassTransit bridge ──
            services.TryAddSingleton<IMassTransitEventHandler, MassTransitEventHandler>();

            // ── Streaming ──
            services.TryAddSingleton<IStreamProvider, MassTransitStreamProvider>();

            // ── Persistence (aligned with LocalRuntime) ──
            // Business state: GAgentBase<TState>.StateStore.Load/Save
            // ★ Missing this causes state loss on Grain deactivation
            services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));

            // Event sourcing storage
            services.TryAddSingleton<IEventStore, InMemoryEventStore>();

            // Agent manifest: module/config restoration
            services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();

            // ── Deduplication ──
            // MassTransit at-least-once → effectively-once via MemoryCache
            services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

            // ── Context (Workflow / Agent context propagation) ──
            services.TryAddSingleton<IRunManager, RunManager>();
            services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();
        });

        return builder;
    }
}
