using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled.WorkflowModules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// DI registration entry point for the scheduled-agent / user-agent-catalog package.
/// </summary>
public static class ScheduledServiceCollectionExtensions
{
    /// <summary>
    /// Registers the User Agent Catalog projection pipeline (materialization runtime,
    /// catalog + Nyx credential projectors, query ports, document metadata, startup
    /// service, and projection stores). Pass <paramref name="configuration"/> so the
    /// document projection store matches the host environment (Elasticsearch in prod,
    /// InMemory for local dev / tests).
    /// </summary>
    public static IServiceCollection AddScheduledAgents(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The helper logs a misconfiguration warning (Console.Error during SCE
        // composition; structured log when a real logger is wired in tests) when
        // configuration is present but Endpoints/Enabled are both empty, so
        // operators see the InMemory fallback at startup.
        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "ScheduledAgents");

        // ─── Retired-actor cleanup contribution ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, ScheduledRetiredActorSpec>());

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
        // Internal-only credential-bearing reader for outbound delivery (issue #466 §D).
        // Architecture rule: NEVER inject IUserAgentDeliveryTargetReader into an
        // IAgentTool implementation; LLM tools see only the caller-scoped public port
        // (which excludes NyxApiKey by DTO shape).
        services.TryAddSingleton<IUserAgentDeliveryTargetReader, UserAgentDeliveryTargetReader>();
        services.TryAddSingleton<UserAgentCatalogProjectionPort>();
        services.TryAddSingleton<IUserAgentCatalogCommandPort, UserAgentCatalogCommandPort>();
        services.TryAddSingleton<ISkillRunnerCommandPort, SkillRunnerCommandPort>();
        services.TryAddSingleton<IWorkflowAgentCommandPort, WorkflowAgentCommandPort>();
        // Caller-scope resolver chain (issue #466 §B). Channel resolver runs first so
        // a request with channel metadata produces the per-sender scope rather than
        // the looser nyxid-scoped tuple from the underlying NyxID session.
        services.TryAddSingleton<INyxIdCurrentUserResolver, NyxIdCurrentUserResolver>();
        services.TryAddSingleton<ChannelMetadataCallerScopeResolver>();
        services.TryAddSingleton<NyxIdNativeCallerScopeResolver>();
        services.TryAddSingleton<ICallerScopeResolver>(sp => new CompositeCallerScopeResolver(new ICallerScopeResolver[]
        {
            sp.GetRequiredService<ChannelMetadataCallerScopeResolver>(),
            sp.GetRequiredService<NyxIdNativeCallerScopeResolver>(),
        }));
        services.AddHostedService<UserAgentCatalogStartupService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, UserAgentCatalogTombstoneCompactionTarget>());

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<UserAgentCatalogDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<UserAgentCatalogDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<UserAgentCatalogNyxCredentialDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
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

        // Register the scheduled-agent workflow module pack so the social_media template's
        // `twitter_publish` step type resolves at workflow run time (issue #216).
        // AddWorkflowModulePack uses TryAddEnumerable, so calling alongside AddAevatarWorkflow
        // is idempotent.
        services.AddScheduledWorkflowExtensions();

        return services;
    }

}
