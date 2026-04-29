using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
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
    /// <summary>
    /// Backwards-compat overload — registers the channel runtime middlewares,
    /// diagnostics, default turn-runner fallback, ChannelBotRegistration projection
    /// pipeline, and pipeline composition without an <see cref="IConfiguration"/>.
    /// Falls back to the InMemory projection store.
    /// </summary>
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
        => AddChannelRuntime(services, configuration: null);

    /// <summary>
    /// Registers the channel runtime middlewares, diagnostics, default turn-runner
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

        // ─── Tombstone compaction options + diagnostics + ES watermark ───
        services.AddOptions<ChannelRuntimeTombstoneCompactionOptions>();
        services.TryAddSingleton<IChannelRuntimeDiagnostics, InMemoryChannelRuntimeDiagnostics>();
        services.TryAddSingleton<IProjectionScopeWatermarkQueryPort, EventStoreProjectionScopeWatermarkQueryPort>();
        if (configuration != null)
        {
            services.Configure<ChannelRuntimeTombstoneCompactionOptions>(
                configuration.GetSection("ChannelRuntime:TombstoneCompaction"));
        }

        // ─── Projection pipeline shared infrastructure ───
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        // Detect projection store provider from configuration. The helper logs a
        // misconfiguration warning (Console.Error during SCE composition; structured
        // log when a real logger is wired in tests) when configuration is present
        // but Endpoints/Enabled are both empty, so operators see the InMemory
        // fallback instead of discovering it after a restart wipes the replica.
        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "ChannelRuntime");

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
        services.TryAddSingleton<ChannelBotRegistrationProjectionPort>();
        services.AddHostedService<ChannelBotRegistrationStartupService>();

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ChannelBotRegistrationDocument, string>(
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
