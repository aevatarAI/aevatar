using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Hosting;

/// <summary>
/// Registers document projection stores (reader + writer) for the Studio-owned
/// current-state readmodels. Mirrors the pattern used by
/// <c>AddGAgentServiceProjectionReadModelProviders</c> (which handles
/// <see cref="UserConfigCurrentStateDocument"/>): either Elasticsearch or
/// InMemory is enabled based on <c>Projection:Document:Providers:*</c>
/// configuration. Required by the actor-backed stores
/// (<c>IRoleCatalogStore</c>, <c>IConnectorCatalogStore</c>,
/// <c>IChatHistoryStore</c>, <c>IGAgentActorRegistryQueryPort</c>,
/// <c>IUserMemoryStore</c>, <c>IStreamingProxyParticipantStore</c>) that read
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

        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for Studio.");
        }

        var selectedDocumentProvider = elasticsearchEnabled
            ? DocumentProviderKind.Elasticsearch
            : DocumentProviderKind.InMemory;
        if (HasAllStudioDocumentReaders(services, selectedDocumentProvider))
            return services;

        if (elasticsearchEnabled)
        {
            RegisterElasticsearch<RoleCatalogCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ConnectorCatalogCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ChatHistoryIndexCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<ChatConversationCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<GAgentRegistryCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<UserMemoryCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StreamingProxyParticipantCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<UserConfigCurrentStateDocument>(services, configuration);
            RegisterElasticsearch<StudioMemberCurrentStateDocument>(services, configuration);
        }
        else
        {
            RegisterInMemory<RoleCatalogCurrentStateDocument>(services);
            RegisterInMemory<ConnectorCatalogCurrentStateDocument>(services);
            RegisterInMemory<ChatHistoryIndexCurrentStateDocument>(services);
            RegisterInMemory<ChatConversationCurrentStateDocument>(services);
            RegisterInMemory<GAgentRegistryCurrentStateDocument>(services);
            RegisterInMemory<UserMemoryCurrentStateDocument>(services);
            RegisterInMemory<StreamingProxyParticipantCurrentStateDocument>(services);
            RegisterInMemory<UserConfigCurrentStateDocument>(services);
            RegisterInMemory<StudioMemberCurrentStateDocument>(services);
        }

        return services;
    }

    private static void RegisterElasticsearch<TDoc>(
        IServiceCollection services,
        IConfiguration configuration)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDoc>(services, DocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TDoc>(services, DocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TDoc, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TDoc>>().Metadata,
            keySelector: readModel => readModel.ActorId,
            keyFormatter: key => key,
            typeRegistry: BuildStudioStateTypeRegistry());
    }

    private static void RegisterInMemory<TDoc>(
        IServiceCollection services)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDoc>(services, DocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TDoc>(services, DocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TDoc, string>(
            keySelector: readModel => readModel.ActorId,
            keyFormatter: key => key,
            defaultSortSelector: readModel => readModel.UpdatedAt);
    }

    private static bool HasAllStudioDocumentReaders(
        IServiceCollection services,
        DocumentProviderKind providerKind)
    {
        return HasDocumentReaderForProvider<RoleCatalogCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ConnectorCatalogCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ChatHistoryIndexCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ChatConversationCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<GAgentRegistryCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<UserMemoryCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StreamingProxyParticipantCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<UserConfigCurrentStateDocument>(services, providerKind)
               && HasDocumentReaderForProvider<StudioMemberCurrentStateDocument>(services, providerKind);
    }

    private static bool HasAnyDocumentReader<TDoc>(IServiceCollection services)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TDoc, string>));
    }

    private static bool HasDocumentReaderForProvider<TDoc>(
        IServiceCollection services,
        DocumentProviderKind providerKind)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        return providerKind switch
        {
            DocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TDoc, string>)),
            DocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TDoc, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleDocumentReaderProvider<TDoc>(
        IServiceCollection services,
        DocumentProviderKind providerKind)
        where TDoc : class, IProjectionReadModel<TDoc>, new()
    {
        if (!HasAnyDocumentReader<TDoc>(services))
            return;
        if (HasDocumentReaderForProvider<TDoc>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TDoc).Name} is already registered with a different provider.");
    }

    private static bool ResolveElasticsearchDocumentEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        var hasEndpoints = section
            .GetSection("Endpoints")
            .GetChildren()
            .Select(x => x.Value?.Trim() ?? string.Empty)
            .Any(x => x.Length > 0);
        return ResolveOptionalBool(explicitEnabled, hasEndpoints);
    }

    private static ElasticsearchProjectionDocumentStoreOptions BuildElasticsearchDocumentOptions(
        IConfiguration configuration)
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Projection:Document:Providers:Elasticsearch is enabled but Endpoints is empty.");
        }

        return options;
    }

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;
        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

    private static TypeRegistry BuildStudioStateTypeRegistry()
    {
        return TypeRegistry.FromMessages(
            UserConfigGAgentState.Descriptor,
            GAgentRegistryState.Descriptor,
            ConnectorCatalogState.Descriptor,
            RoleCatalogState.Descriptor,
            UserMemoryState.Descriptor,
            StreamingProxyParticipantGAgentState.Descriptor,
            ChatHistoryIndexState.Descriptor,
            ChatConversationState.Descriptor,
            StudioMemberState.Descriptor);
    }

    private enum DocumentProviderKind
    {
        InMemory,
        Elasticsearch,
    }
}
