using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Configuration;
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
    ///   <item>the document projection store (Elasticsearch in prod, InMemory for dev / tests when no configuration is supplied).</item>
    /// </list>
    /// Pass <paramref name="configuration"/> so the document store choice matches the
    /// host environment — production must use Elasticsearch (multi-pod safe, persistent
    /// across restarts); the unconditional InMemory store made policies pod-local and
    /// vanish on restart.
    /// </summary>
    public static IServiceCollection AddChatRoutingAgents(
        this IServiceCollection services, IConfiguration? configuration = null)
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

        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ChatRoutePolicyCurrentStateDocument>,
            ChatRoutePolicyDocumentMetadataProvider>();

        // Per-scope projection activation port. Callers (admin endpoint write
        // path) must call EnsureProjectionForActorAsync(actorId) after
        // CreateAsync<ChatRoutePolicyGAgent> and before dispatching the
        // Upsert / Remove command — otherwise committed events fire into the
        // void and the readmodel never materializes.
        services.TryAddSingleton<ChatRoutePolicyProjectionPort>();

        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "ChatRouting");

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<ChatRoutePolicyCurrentStateDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ChatRoutePolicyCurrentStateDocument>>().Metadata,
                keySelector: static doc => doc.ActorId,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ChatRoutePolicyCurrentStateDocument, string>(
                static document => document.ActorId,
                static key => key);
        }

        return services;
    }
}
