using Aevatar.AI.Abstractions.Middleware;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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

        // Detect projection store provider from configuration
        var useElasticsearch = ResolveElasticsearchEnabled(configuration);

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
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILLMCallMiddleware, ChannelContextMiddleware>());
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

    /// <summary>
    /// Detects whether Elasticsearch is the projection store.
    /// Reuses the same detection logic as Scripting projections:
    /// explicit Enabled=true, or auto-detect from Endpoints presence.
    /// When configuration is null (unit tests), falls back to InMemory.
    /// When configuration is present but ES is not configured, logs a warning
    /// because production should always use ES.
    /// </summary>
    private static bool ResolveElasticsearchEnabled(IConfiguration? configuration)
    {
        if (configuration == null) return false;

        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        if (!string.IsNullOrWhiteSpace(explicitEnabled))
            return string.Equals(explicitEnabled.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        // Auto-detect: if endpoints are configured, ES is enabled
        var hasEndpoints = section.GetSection("Endpoints").GetChildren()
            .Any(x => !string.IsNullOrWhiteSpace(x.Value));

        if (!hasEndpoints)
        {
            // Not a test (configuration is present) but no ES configured.
            // This is expected for local dev, but a misconfiguration in prod.
            Console.Error.WriteLine(
                "[WARN] ChannelRuntime: Elasticsearch not configured — using volatile InMemory projection store. " +
                "Registration data will be lost on restart. Set Projection:Document:Providers:Elasticsearch:Enabled=true for production.");
        }

        return hasEndpoints;
    }

    private static Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration.ElasticsearchProjectionDocumentStoreOptions
        BuildElasticsearchOptions(IConfiguration configuration)
    {
        var options = new Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration.ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        return options;
    }
}
