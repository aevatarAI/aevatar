using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChannelRuntime;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers channel runtime services. Pass IConfiguration so the document
    /// projection store matches the host environment (Elasticsearch in prod,
    /// InMemory for local dev / tests).
    /// </summary>
    public static IServiceCollection AddChannelRuntime(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        // Memory cache for webhook dedup
        services.AddMemoryCache();
        services.TryAddSingleton<IChannelRuntimeDiagnostics, InMemoryChannelRuntimeDiagnostics>();

        // Projection pipeline shared infrastructure
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        // Detect projection store provider from configuration
        var useElasticsearch = ResolveElasticsearchEnabled(configuration);

        // ─── Device Registration projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<DeviceRegistrationMaterializationContext>>(
            static scopeKey => new DeviceRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new DeviceRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>,
            DeviceRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IDeviceRegistrationQueryPort, DeviceRegistrationQueryPort>();

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<DeviceRegistrationDocument, string>(
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<DeviceRegistrationDocument, string>(
                static doc => doc.Id, static key => key);
        }

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

        // ─── Agent Registry projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            AgentRegistryMaterializationContext,
            AgentRegistryMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<AgentRegistryMaterializationContext>>(
            static scopeKey => new AgentRegistryMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new AgentRegistryMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            AgentRegistryMaterializationContext,
            AgentRegistryProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<AgentRegistryDocument>,
            AgentRegistryDocumentMetadataProvider>();
        services.TryAddSingleton<IAgentRegistryQueryPort, AgentRegistryQueryPort>();
        services.TryAddSingleton<AgentRegistryProjectionPort>();
        services.AddHostedService<AgentRegistryStartupService>();

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<AgentRegistryDocument, string>(
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<AgentRegistryDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<AgentRegistryDocument, string>(
                static doc => doc.Id, static key => key);
        }

        // Register platform adapters
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, LarkPlatformAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, TelegramPlatformAdapter>());

        services.Replace(ServiceDescriptor.Singleton<IHumanInteractionPort, FeishuCardHumanInteractionPort>());

        // channel runtime tools
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ChannelRegistrationToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, AgentDeliveryTargetToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, AgentBuilderToolSource>());

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
