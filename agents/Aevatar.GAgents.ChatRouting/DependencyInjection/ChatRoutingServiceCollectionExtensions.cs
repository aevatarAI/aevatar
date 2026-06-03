using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// DI registration entry point for the ChatRouting agent package
/// (ingress layer v1 — issue #692, Phase 1).
/// </summary>
// Refactor (iter32/cluster-034-chat-route-policy-request-path-projection-activation):
//   Old pattern: Chat route policy admin endpoints + voice demo bootstrap 在 request path 调 EnsureProjectionForActorAsync 同步 priming projection,违反 query-time priming forbidden + 命令骨架内聚
//   New principle: 加 ChatRoutePolicyCommittedStateProjectionActivationPlanProvider(committed-state hook 触发);删 ChatRoutePolicyProjectionPort + request-path activation;DI 注册 dispatcher + hook + provider;query_projection_priming_guard 加 chat route policy endpoint 扫描
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
        // Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
        //   Old pattern: Mainnet Host endpoints registered actor runtime/dispatch and performed command envelope assembly in request handlers.
        //   New principle: DI exposes the chat route policy Application command port so Host composes the port instead of runtime internals.
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
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ChatRoutePolicyCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<IChatRoutePolicyCommandPort, ChatRoutePolicyCommandPort>();

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
