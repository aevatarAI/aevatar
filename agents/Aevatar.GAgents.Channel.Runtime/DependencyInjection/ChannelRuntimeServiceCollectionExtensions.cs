using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// DI registration entry point for the channel runtime package.
/// </summary>
public static class ChannelRuntimeServiceCollectionExtensions
{
    // Refactor (iter36/cluster-042-channel-diagnostics-readmodel):
    //   Old pattern: Channel runtime diagnostics 用 singleton in-memory list with retention trimming;diagnostics endpoint 读 process-local list 直接(InMemoryChannelRuntimeDiagnostics 注册为 singleton + ImmutableList 字段 + ImmutableInterlocked mutation)。
    //   New principle: Channel diagnostics 改为 logs/metrics only(observability path)OR actor/projection-backed diagnostic events with readmodel query。**禁止** public endpoint 读 singleton process memory 作 diagnostic fact source。
    /// <summary>
    /// Backwards-compat overload — registers the channel runtime middlewares,
    /// default turn-runner fallback, ChannelBotRegistration projection
    /// pipeline, and pipeline composition without an <see cref="IConfiguration"/>.
    /// Falls back to the InMemory projection store.
    /// </summary>
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
        => AddChannelRuntime(services, configuration: null);

    /// <summary>
    /// Registers the channel runtime middlewares, default turn-runner
    /// fallback, ChannelBotRegistration projection pipeline, and pipeline composition.
    /// Pass <paramref name="configuration"/> so the document projection store matches
    /// the host environment (Elasticsearch in prod, InMemory for local dev / tests).
    /// </summary>
    public static IServiceCollection AddChannelRuntime(
        this IServiceCollection services, IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ─── Retired-actor cleanup contribution ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, ChannelRuntimeRetiredActorSpec>());

        // ─── Core middlewares + default turn runner ───
        services.TryAddSingleton<ConversationResolverMiddleware>();
        services.TryAddSingleton<LoggingMiddleware>();
        services.TryAddSingleton<TracingMiddleware>();
        services.TryAddSingleton<IConversationTurnRunner, NullConversationTurnRunner>();
        services.TryAddSingleton<IConversationCardTurnRunner, NullConversationCardTurnRunner>();

        // ─── Tombstone compaction options + materialized watermark ───
        services.AddOptions<ChannelRuntimeTombstoneCompactionOptions>();
        // Refactor (iter17/cluster-034):
        //   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
        //   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
        services.AddProjectionScopeStatusRuntimeCore();
        if (configuration != null)
        {
            services.Configure<ChannelRuntimeTombstoneCompactionOptions>(
                configuration.GetSection("ChannelRuntime:TombstoneCompaction"));
        }

        // ─── Projection pipeline shared infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider>());

        var documentProvider = configuration is null
            ? new ProjectionDocumentProviderSelection(
                ProjectionDocumentProviderKind.InMemory,
                ElasticsearchEnabled: false,
                InMemoryEnabled: true)
            : ProjectionDocumentProviderConfiguration.Resolve(configuration, "ChannelRuntime");

        // ─── Channel Bot Registration projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ChannelBotRegistrationMaterializationContext>>(
            static scopeKey => new ChannelBotRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ChannelBotRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>,
            ChannelBotRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IChannelBotRegistrationQueryPort, ChannelBotRegistrationQueryPort>();
        services.TryAddSingleton<IChannelBotRegistrationQueryByNyxIdentityPort, ChannelBotRegistrationQueryPort>();
        services.TryAddSingleton<IChannelBotRegistrationRuntimeQueryPort, ChannelBotRegistrationRuntimeQueryPort>();
        services.TryAddSingleton<ChannelBotRegistrationProjectionBootstrapActivator>();
        services.AddHostedService<ChannelBotRegistrationStartupService>();

        if (documentProvider.ElasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
                optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<ProjectionScopeStatusDocument, string>(
                optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
                static doc => doc.Id, static key => key);
            services.AddInMemoryDocumentProjectionStore<ProjectionScopeStatusDocument, string>(
                static doc => doc.Id, static key => key);
        }

        // ─── Channel pipeline composition ───
        services.TryAddSingleton<ConversationDispatchMiddleware>();
        services.Replace(ServiceDescriptor.Singleton(_ => new MiddlewarePipelineBuilder()
            .Use<TracingMiddleware>()
            .Use<LoggingMiddleware>()
            .Use<ConversationResolverMiddleware>()
            .Use<ConversationDispatchMiddleware>()));
        services.TryAddSingleton<ChannelPipeline>(sp => sp.GetRequiredService<MiddlewarePipelineBuilder>().Build(sp));

        // ─── Tombstone compaction service ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, ChannelBotRegistrationTombstoneCompactionTarget>());
        services.TryAddSingleton<ChannelRuntimeTombstoneCompactor>();
        services.AddHostedService<ChannelRuntimeTombstoneCompactionService>();

        return services;
    }

}
