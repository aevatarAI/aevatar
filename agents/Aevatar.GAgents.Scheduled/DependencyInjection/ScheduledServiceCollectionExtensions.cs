using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
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

        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            logger: null,
            storeName: "ScheduledAgents");

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
        services.TryAddSingleton<IUserAgentCatalogCommandPort, UserAgentCatalogCommandPort>();
        services.TryAddSingleton<ISkillRunnerCommandPort, SkillRunnerCommandPort>();
        services.TryAddSingleton<IWorkflowAgentCommandPort, WorkflowAgentCommandPort>();
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

        return services;
    }

}
