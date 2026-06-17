using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StreamingProxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Mainnet.Host.Api.Hosting;

public static class MainnetAgentProjectionDocumentStoresExtensions
{
    public static IServiceCollection AddMainnetAgentProjectionDocumentStores(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(
            configuration,
            "MainnetAgentProjectionDocumentStores");

        if (documentProvider.ElasticsearchEnabled)
        {
            AddElasticsearchStores(services, configuration);
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AevatarOAuthClientEsAclStartupGuard>());
            // Self-heal projection-index schema drift at startup (reindex + atomic alias swap)
            // so a deploy that bumps a read-model schema doesn't 500 reads (e.g. /ws/voice).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ElasticsearchProjectionIndexReconcileHostedService>());
        }
        else
        {
            AddInMemoryStores(services);
        }

        return services;
    }

    private static void AddElasticsearchStores(IServiceCollection services, IConfiguration configuration)
    {
        TryAddElasticsearchStore<ChannelBotRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ProjectionScopeStatusDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ExternalIdentityBindingDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<AevatarOAuthClientDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ChatRoutePolicyCurrentStateDocument>(services, configuration, static document => document.ActorId);
        TryAddElasticsearchStore<DeviceRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<SkillRunnerExecutionDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogNyxCredentialDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<HealthProbeTargetDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyChatSessionTerminalSnapshot>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyRoomParticipantsSnapshot>(services, configuration, static document => document.Id);
    }

    private static void AddInMemoryStores(IServiceCollection services)
    {
        TryAddInMemoryStore<ChannelBotRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ProjectionScopeStatusDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ExternalIdentityBindingDocument>(services, static document => document.Id);
        TryAddInMemoryStore<AevatarOAuthClientDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ChatRoutePolicyCurrentStateDocument>(services, static document => document.ActorId);
        TryAddInMemoryStore<DeviceRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogDocument>(services, static document => document.Id);
        TryAddInMemoryStore<SkillRunnerExecutionDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogNyxCredentialDocument>(services, static document => document.Id);
        TryAddInMemoryStore<HealthProbeTargetDocument>(services, static document => document.Id);
        TryAddInMemoryStore(
            services,
            static (StreamingProxyChatSessionTerminalSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
        TryAddInMemoryStore(
            services,
            static (StreamingProxyRoomParticipantsSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
    }

    private static void TryAddElasticsearchStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryStore<TReadModel>(
        IServiceCollection services,
        Func<TReadModel, string> keySelector,
        Func<TReadModel, object?>? defaultSortSelector = null)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: defaultSortSelector);
    }

    private static void EnsureCompatibleDocumentReaderProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (!HasAnyDocumentReader<TReadModel>(services))
            return;
        if (HasDocumentReaderForProvider<TReadModel>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TReadModel).Name} is already registered with a different provider.");
    }

    private static bool HasAnyDocumentReader<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<TReadModel, string>));
    }

    private static bool HasDocumentReaderForProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TReadModel, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(InMemoryProjectionDocumentStore<TReadModel, string>)),
            _ => false,
        };
    }
}
