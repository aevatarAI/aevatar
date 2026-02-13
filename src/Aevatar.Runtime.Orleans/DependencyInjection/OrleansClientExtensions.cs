// ─────────────────────────────────────────────────────────────
// OrleansClientExtensions - Client-side DI registration.
// Registers IActorRuntime (Orleans), IStreamProvider (MassTransit),
// and IAgentManifestStore for agent indexing.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Actor;
using Aevatar.Orleans.Stream;
using Aevatar.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Orleans.DependencyInjection;

/// <summary>Client-side DI extensions for Orleans runtime.</summary>
public static class OrleansClientExtensions
{
    /// <summary>
    /// Registers Orleans-backed actor runtime for the Client process.
    /// Requires IClusterClient (from Orleans Client configuration) and
    /// MassTransit (IBus, ISendEndpointProvider) to be configured.
    /// </summary>
    public static IServiceCollection AddAevatarOrleansClient(
        this IServiceCollection services)
    {
        // Actor runtime
        services.TryAddSingleton<IActorRuntime, OrleansActorRuntime>();

        // Stream provider: MassTransit-backed (event ingress + result subscription)
        services.TryAddSingleton<IStreamProvider, MassTransitStreamProvider>();

        // Agent manifest index (default: in-memory; replace with persistent impl)
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();

        return services;
    }
}
