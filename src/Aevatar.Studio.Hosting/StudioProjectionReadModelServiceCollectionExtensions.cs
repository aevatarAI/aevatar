using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.Studio.Infrastructure.ProjectionRecovery;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Hosting;

/// <summary>
/// Registers document projection stores (reader + writer) for the Studio-owned
/// current-state readmodels. Mirrors the pattern used by
/// <c>AddGAgentServiceProjectionReadModelProviders</c> (which handles
/// <see cref="UserConfigCurrentStateDocument"/>): either Elasticsearch or
/// InMemory is enabled based on <c>Projection:Document:Providers:*</c>
/// configuration. Required by the actor-backed stores
/// (<c>IRoleCatalogQueryPort</c>, <c>IConnectorCatalogQueryPort</c>,
/// <c>IChatHistoryQueryPort</c>, <c>IGAgentActorRegistryQueryPort</c>,
/// <c>IUserMemoryQueryPort</c>) that read
/// from these documents via <c>IProjectionDocumentReader</c>.
/// </summary>
internal static class StudioProjectionReadModelServiceCollectionExtensions
{
    public static IServiceCollection AddStudioProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "Studio");
        if (HasAllStudioDocumentReaders(services, documentProvider.Kind))
            return services;

        if (documentProvider.ElasticsearchEnabled)
        {
            RegisterElasticsearch<WorkflowExecutionBoardDocument>(
                services,
                configuration,
                static document => document.RootActorId);
            RegisterElasticsearch<ScopeWorkflowCatalogueSourceDocument>(
                services,
                configuration,
                static document => document.Id);
            RegisterElasticsearch<ScopeWorkflowCatalogueRowDocument>(
                services,
                configuration,
                static document => document.Id);
            RegisterElasticsearch<RoleCatalogCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ConnectorCatalogCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ChatConversationCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<NyxIdChatConversationCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ChatHistoryCreateRecoveryCurrentStateDocument>(
                services,
                configuration,
                static document => document.Id);
            RegisterElasticsearch<GAgentRegistryCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<UserMemoryCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<UserConfigCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<LLMModelCatalogPolicyCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StudioMemberCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StudioMemberBindingRunCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StudioTeamCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StudioWorkspaceCurrentStateDocument>(services, configuration);
            services.AddElasticsearchDocumentProjectionRepairStore<
                StudioWorkspaceCurrentStateDocument,
                string>();
            services.TryAddSingleton<
                IStudioWorkspaceVersionRegressionStorePort,
                ElasticsearchStudioWorkspaceVersionRegressionStorePort>();
            services.TryAddSingleton<
                IStudioWorkspaceVersionRegressionRepairService,
                StudioWorkspaceVersionRegressionRepairService>();
            RegisterElasticsearch<ContentArtifactCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ContentArtifactPinCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<WorkOrderCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<WorkflowDeliveryCurrentStateDocument>(services, configuration);
        }
        else
        {
            RegisterInMemory<WorkflowExecutionBoardDocument>(
                services,
                static document => document.RootActorId,
                static document => document.UpdatedAt);
            RegisterInMemory<ScopeWorkflowCatalogueSourceDocument>(
                services,
                static document => document.Id,
                static document => document.UpdatedAt);
            RegisterInMemory<ScopeWorkflowCatalogueRowDocument>(
                services,
                static document => document.Id,
                static document => document.UpdatedAt);
            RegisterInMemory<RoleCatalogCurrentStateDocument>(services);
            RegisterInMemory<ConnectorCatalogCurrentStateDocument>(services);
            RegisterInMemory<ChatConversationCurrentStateDocument>(services);
            RegisterInMemory<NyxIdChatConversationCurrentStateDocument>(services);
            RegisterInMemory<ChatHistoryCreateRecoveryCurrentStateDocument>(
                services,
                static document => document.Id,
                static document => document.UpdatedAt);
            RegisterInMemory<GAgentRegistryCurrentStateDocument>(services);
            RegisterInMemory<UserMemoryCurrentStateDocument>(services);
            RegisterInMemory<UserConfigCurrentStateDocument>(services);
            RegisterInMemory<LLMModelCatalogPolicyCurrentStateDocument>(services);
            RegisterInMemory<StudioMemberCurrentStateDocument>(services);
            RegisterInMemory<StudioMemberBindingRunCurrentStateDocument>(services);
            RegisterInMemory<StudioTeamCurrentStateDocument>(services);
            RegisterInMemory<StudioWorkspaceCurrentStateDocument>(services);
            RegisterInMemory<ContentArtifactCurrentStateDocument>(services);
            RegisterInMemory<ContentArtifactPinCurrentStateDocument>(services);
            RegisterInMemory<WorkOrderCurrentStateDocument>(services);
            RegisterInMemory<WorkflowDeliveryCurrentStateDocument>(services);
        }

        return services;
    }

    private static void RegisterElasticsearch<TDoc>(
        IServiceCollection services,
        IConfiguration configuration)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        RegisterElasticsearch<TDoc>(
            services,
            configuration,
            static readModel => readModel.ActorId);
    }

    private static void RegisterElasticsearch<TDoc>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TDoc, string> keySelector)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDoc>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TDoc>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TDoc, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TDoc>>().Metadata,
            keySelector: keySelector,
            keyFormatter: key => key,
            typeRegistry: BuildStudioStateTypeRegistry());
    }

    private static void RegisterInMemory<TDoc>(
        IServiceCollection services)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        RegisterInMemory<TDoc>(
            services,
            static readModel => readModel.ActorId,
            static readModel => readModel.UpdatedAt);
    }

    private static void RegisterInMemory<TDoc>(
        IServiceCollection services,
        Func<TDoc, string> keySelector,
        Func<TDoc, object?> defaultSortSelector)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDoc>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TDoc>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TDoc, string>(
            keySelector: keySelector,
            keyFormatter: key => key,
            defaultSortSelector: defaultSortSelector);
    }

    private static bool HasAllStudioDocumentReaders(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
    {
        return HasDocumentReaderForProvider<WorkflowExecutionBoardDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ScopeWorkflowCatalogueSourceDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ScopeWorkflowCatalogueRowDocument>(services, providerKind)
               && HasDocumentReaderForProvider<RoleCatalogCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ConnectorCatalogCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ChatConversationCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<NyxIdChatConversationCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ChatHistoryCreateRecoveryCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<GAgentRegistryCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<UserMemoryCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<UserConfigCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<LLMModelCatalogPolicyCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StudioMemberCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StudioMemberBindingRunCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StudioTeamCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StudioWorkspaceCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ContentArtifactCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ContentArtifactPinCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<WorkOrderCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<WorkflowDeliveryCurrentStateDocument>(services, providerKind);
    }

    private static bool HasAnyDocumentReader<TDoc>(IServiceCollection services)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TDoc, string>));
    }

    private static bool HasDocumentReaderForProvider<TDoc>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TDoc, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TDoc, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleDocumentReaderProvider<TDoc>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        if (!HasAnyDocumentReader<TDoc>(services))
            return;
        if (HasDocumentReaderForProvider<TDoc>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TDoc).Name} is already registered with a different provider.");
    }

    private static TypeRegistry BuildStudioStateTypeRegistry()
    {
        return TypeRegistry.FromMessages(
            UserConfigGAgentState.Descriptor,
            LLMModelCatalogPolicyGAgentState.Descriptor,
            GAgentRegistryState.Descriptor,
            ConnectorCatalogState.Descriptor,
            RoleCatalogState.Descriptor,
            UserMemoryState.Descriptor,
            ChatConversationState.Descriptor,
            ChatTurnHistoryDeliveryState.Descriptor,
            StudioMemberState.Descriptor,
            StudioMemberBindingRunState.Descriptor,
            StudioTeamState.Descriptor,
            StudioWorkspaceState.Descriptor,
            ContentArtifactState.Descriptor,
            ContentArtifactPinState.Descriptor,
            WorkOrderState.Descriptor,
            WorkflowDeliveryState.Descriptor);
    }

}
