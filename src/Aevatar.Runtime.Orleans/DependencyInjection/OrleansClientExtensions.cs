// ─────────────────────────────────────────────────────────────
// OrleansClientExtensions - Client-side DI registration.
//
// Two levels of API:
//   1. AddAevatarOrleansClient(IServiceCollection)
//      → Just register Orleans services (IActorRuntime, IStreamProvider, etc.)
//      → Caller must configure Orleans Client + MassTransit separately.
//
//   2. UseAevatarOrleansRuntime(IHostApplicationBuilder, ...)
//      → One-call full setup: Orleans Client + MassTransit/Kafka + services.
//      → For application hosts (Api, Gateway) that want Orleans as runtime.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Actors;
using Aevatar.Orleans.Streaming;
using Aevatar.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;

namespace Aevatar.Orleans.DependencyInjection;

/// <summary>Client-side DI extensions for Orleans runtime.</summary>
public static class OrleansClientExtensions
{
    /// <summary>
    /// Registers Orleans-backed actor runtime services for the Client process.
    /// Requires IGrainFactory (from Orleans Client or Silo host) and
    /// IAgentEventSender (from MassTransit/Kafka setup) to be configured.
    /// </summary>
    public static IServiceCollection AddAevatarOrleansClient(
        this IServiceCollection services)
    {
        // Actor runtime (depends on IGrainFactory via DI)
        services.TryAddSingleton<IActorRuntime, OrleansActorRuntime>();

        // Stream provider: MassTransit-backed (event ingress)
        services.TryAddSingleton<IStreamProvider, MassTransitStreamProvider>();

        // Agent manifest index (default: in-memory; replace with persistent impl)
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();

        return services;
    }

    /// <summary>
    /// One-call full setup for Orleans Client process:
    /// Orleans Client host + MassTransit/Kafka + all services.
    /// <para>
    /// After calling this, also call <c>AddAevatarRuntime()</c> to fill in
    /// remaining common services (context, deduplication, etc.) — TryAdd
    /// ensures Orleans-specific registrations are not overridden.
    /// </para>
    /// </summary>
    /// <param name="builder">Host application builder.</param>
    /// <param name="kafkaBootstrap">Kafka bootstrap servers (default: localhost:9092).</param>
    /// <param name="clusterId">Orleans cluster ID (default: aevatar-dev).</param>
    /// <param name="serviceId">Orleans service ID (default: aevatar).</param>
    public static IHostApplicationBuilder UseAevatarOrleansRuntime(
        this IHostApplicationBuilder builder,
        string? kafkaBootstrap = null,
        string? clusterId = null,
        string? serviceId = null)
    {
        var kafka = kafkaBootstrap ?? "localhost:9092";
        var cluster = clusterId ?? "aevatar-dev";
        var service = serviceId ?? "aevatar";

        // ── Orleans Client ──
        builder.UseOrleansClient(clientBuilder =>
        {
            clientBuilder
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(opts =>
                {
                    opts.ClusterId = cluster;
                    opts.ServiceId = service;
                });
        });

        // ── MassTransit + Kafka (producer only) ──
        builder.Services.AddAevatarKafkaClient(kafka);

        // ── Orleans actor runtime services ──
        builder.Services.AddAevatarOrleansClient();

        return builder;
    }
}
