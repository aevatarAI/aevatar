using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// DI registration entry point for the ChatRouting agent package
/// (ingress layer v1 — issue #692, Phase 1).
/// </summary>
public static class ChatRoutingServiceCollectionExtensions
{
    /// <summary>
    /// Wires the ChatRoutePolicy projection pipeline so that committed
    /// <c>ChatRoutePolicyUpdated</c> events from <see cref="ChatRoutePolicyGAgent"/>
    /// flow through the materialization runtime into <see cref="ChatRoutePolicyCurrentStateProjector"/>
    /// and land in <see cref="ChatRoutePolicyCurrentStateDocument"/>:
    /// <list type="bullet">
    ///   <item>materialization runtime + scope + per-scope lease,</item>
    ///   <item>the current-state projector as an <see cref="Aevatar.CQRS.Projection.Core.Abstractions.ICurrentStateProjectionMaterializer{TContext}"/> contributor,</item>
    ///   <item>the InMemory document projection store (reader + writer).</item>
    /// </list>
    /// Without the runtime and materializer registrations the projector would
    /// have a backing store but no subscribed materialization, so the readmodel
    /// would silently never populate (the projection-store-registration pitfall).
    ///
    /// Phase 1 wires the InMemory store unconditionally; the Elasticsearch-vs-InMemory
    /// selection (mirroring <c>AddScheduledAgents</c>) lands when an ingress entry
    /// actually consumes the readmodel.
    /// </summary>
    public static IServiceCollection AddChatRoutingAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Shared projection plumbing used by the projector (write dispatcher +
        // clock). Both registrations are TryAdd so they're safe alongside other
        // agents that already wire them.
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddProjectionMaterializationRuntimeCore<
            ChatRoutePolicyMaterializationContext,
            ChatRoutePolicyMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ChatRoutePolicyMaterializationContext>>(
            static scopeKey => new ChatRoutePolicyMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ChatRoutePolicyMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ChatRoutePolicyMaterializationContext,
            ChatRoutePolicyCurrentStateProjector>();

        services.AddInMemoryDocumentProjectionStore<ChatRoutePolicyCurrentStateDocument, string>(
            static document => document.ActorId,
            static key => key);

        return services;
    }
}
