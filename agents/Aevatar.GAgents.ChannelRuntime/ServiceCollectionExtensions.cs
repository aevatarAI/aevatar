using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.ChannelRuntime.Outbound;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        Aevatar.GAgents.Channel.Runtime.ChannelRuntimeServiceCollectionExtensions.AddChannelRuntime(services);

        services.AddOptions<ChannelRuntimeTombstoneCompactionOptions>();
        services.TryAddSingleton<IChannelRuntimeDiagnostics, InMemoryChannelRuntimeDiagnostics>();
        services.TryAddSingleton<IProjectionScopeWatermarkQueryPort, EventStoreProjectionScopeWatermarkQueryPort>();
        if (configuration != null)
        {
            services.Configure<ChannelRuntimeTombstoneCompactionOptions>(
                configuration.GetSection("ChannelRuntime:TombstoneCompaction"));
        }

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
        services.TryAddSingleton<DeviceRegistrationProjectionPort>();
        services.AddHostedService<DeviceRegistrationStartupService>();

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
        services.TryAddSingleton<IChannelBotRegistrationQueryByNyxIdentityPort, ChannelBotRegistrationQueryPort>();
        services.TryAddSingleton<Aevatar.GAgents.NyxidChat.INyxIdRelayScopeResolver, NyxIdRelayScopeResolver>();
        services.TryAddSingleton<IChannelBotRegistrationRuntimeQueryPort, ChannelBotRegistrationRuntimeQueryPort>();
        services.TryAddSingleton<ChannelBotRegistrationProjectionPort>();
        services.TryAddSingleton<ChannelPlatformReplyService>();
        services.TryAddSingleton<INyxLarkProvisioningService, NyxLarkProvisioningService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxLarkProvisioningService>());
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

        // ─── User Agent Catalog projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<UserAgentCatalogMaterializationContext>>(
            static scopeKey => new UserAgentCatalogMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new UserAgentCatalogMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogProjector>();
        services.AddCurrentStateProjectionMaterializer<
            UserAgentCatalogMaterializationContext,
            UserAgentCatalogNyxCredentialProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<UserAgentCatalogDocument>,
            UserAgentCatalogDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<UserAgentCatalogNyxCredentialDocument>,
            UserAgentCatalogNyxCredentialDocumentMetadataProvider>();
        services.TryAddSingleton<IUserAgentCatalogQueryPort, UserAgentCatalogQueryPort>();
        services.TryAddSingleton<IUserAgentCatalogRuntimeQueryPort, UserAgentCatalogRuntimeQueryPort>();
        services.TryAddSingleton<UserAgentCatalogProjectionPort>();
        services.AddHostedService<UserAgentCatalogStartupService>();

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<UserAgentCatalogDocument, string>(
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<UserAgentCatalogDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<UserAgentCatalogNyxCredentialDocument, string>(
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<UserAgentCatalogNyxCredentialDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<UserAgentCatalogDocument, string>(
                static doc => doc.Id, static key => key);
            services.AddInMemoryDocumentProjectionStore<UserAgentCatalogNyxCredentialDocument, string>(
                static doc => doc.Id, static key => key);
        }

        services.Replace(ServiceDescriptor.Singleton<IHumanInteractionPort, FeishuCardHumanInteractionPort>());

        // channel runtime tools
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ChannelRegistrationToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, AgentDeliveryTargetToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, AgentBuilderToolSource>());

        // interactive reply composer registry, collector, dispatcher, and LLM-facing tool
        services.AddChannelInteractiveReplyTools();
        services.TryAddSingleton<IInteractiveReplyDispatcher, NyxIdRelayInteractiveReplyDispatcher>();
        services.TryAddSingleton<ChannelRuntimeTombstoneCompactor>();
        services.AddHostedService<ChannelRuntimeTombstoneCompactionService>();

        services.AddHttpClient(LarkConversationHostDefaults.HttpClientName, client =>
        {
            client.BaseAddress = LarkConversationHostDefaults.BaseAddress;
        });
        services.TryAddSingleton<LarkMessageComposer>();
        services.TryAddSingleton<LarkChannelNativeMessageProducer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageComposer, LarkMessageComposer>(
            sp => sp.GetRequiredService<LarkMessageComposer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageProducer, LarkChannelNativeMessageProducer>(
            sp => sp.GetRequiredService<LarkChannelNativeMessageProducer>()));
        services.TryAddSingleton<LarkPayloadRedactor>();
        services.TryAddSingleton<NyxIdRelayOutboundPort>();
        services.TryAddSingleton<INyxIdRelayReplayGuard>(sp =>
        {
            var relayOptions = sp.GetService<NyxIdRelayOptions>() ?? new NyxIdRelayOptions();
            return new NyxIdRelayReplayGuard(
                TimeSpan.FromSeconds(Math.Max(1, relayOptions.CallbackReplayWindowSeconds)),
                TimeProvider.System);
        });
        services.TryAddSingleton<ConversationDispatchMiddleware>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILLMCallMiddleware, ChannelContextMiddleware>());
        services.Replace(ServiceDescriptor.Singleton<IConversationTurnRunner, ChannelConversationTurnRunner>());
        services.Replace(ServiceDescriptor.Singleton(_ => new MiddlewarePipelineBuilder()
            .Use<TracingMiddleware>()
            .Use<LoggingMiddleware>()
            .Use<ConversationResolverMiddleware>()
            .Use<ConversationDispatchMiddleware>()));
        services.TryAddSingleton<ChannelPipeline>(sp => sp.GetRequiredService<MiddlewarePipelineBuilder>().Build(sp));
        services.TryAddSingleton<IConversationReplyGenerator, NyxIdConversationReplyGenerator>();
        services.TryAddSingleton<ChannelLlmReplyInboxRuntime>();
        services.TryAddSingleton<IChannelLlmReplyInbox>(sp => sp.GetRequiredService<ChannelLlmReplyInboxRuntime>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ChannelLlmReplyInboxHostedService>());
        services.TryAddSingleton<LarkConversationInboxRuntime>();
        services.TryAddSingleton<ILarkConversationInbox>(sp => sp.GetRequiredService<LarkConversationInboxRuntime>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LarkConversationInboxHostedService>());

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
