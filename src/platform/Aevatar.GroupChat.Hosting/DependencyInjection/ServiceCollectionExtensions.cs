using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.DependencyInjection;
using Aevatar.GroupChat.Hosting.Configuration;
using Aevatar.GroupChat.Hosting.Feeds;
using Aevatar.GroupChat.Hosting.Participants;
using Aevatar.GroupChat.Hosting.Workers;
using Aevatar.GroupChat.Projection.DependencyInjection;
using Aevatar.GroupChat.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GroupChat.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroupChatCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGroupChatApplication();
        services.AddGroupChatProjection();
        services.AddGroupChatProjectionReadModelProviders(configuration);
        services.AddOptions<GroupChatCapabilityOptions>()
            .Bind(configuration.GetSection(GroupChatCapabilityOptions.SectionName));
        services.Replace(ServiceDescriptor.Singleton<IAgentFeedInterestEvaluator, ConfiguredAgentFeedInterestEvaluator>());
        services.Replace(ServiceDescriptor.Singleton<IParticipantReplyGenerationPort, DemoParticipantReplyGenerationPort>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GroupChatWorkerHostedService>());
        return services;
    }

    public static IServiceCollection AddGroupChatProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<GroupTimelineReadModel, string>)) &&
            services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<AgentFeedReadModel, string>)) &&
            services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<SourceCatalogReadModel, string>)))
            return services;

        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for GroupChat.");
        }

        if (elasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<GroupTimelineReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<GroupTimelineReadModel>>().Metadata,
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<AgentFeedReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<AgentFeedReadModel>>().Metadata,
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key);
            services.AddElasticsearchDocumentProjectionStore<SourceCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<SourceCatalogReadModel>>().Metadata,
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<GroupTimelineReadModel, string>(
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key,
                defaultSortSelector: static readModel => readModel.UpdatedAt,
                queryTakeMax: 200);
            services.AddInMemoryDocumentProjectionStore<AgentFeedReadModel, string>(
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key,
                defaultSortSelector: static readModel => readModel.UpdatedAt,
                queryTakeMax: 200);
            services.AddInMemoryDocumentProjectionStore<SourceCatalogReadModel, string>(
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key,
                defaultSortSelector: static readModel => readModel.UpdatedAt,
                queryTakeMax: 200);
        }

        return services;
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
}
