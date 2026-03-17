using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Infrastructure.Artifacts;
using Aevatar.GAgentService.Infrastructure.Dispatch;
using Aevatar.GAgentService.Hosting.Demo;
using Aevatar.GAgentService.Governance.Hosting.DependencyInjection;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!services.Any(x => x.ServiceType == typeof(IScriptEvolutionProposalPort)))
            services.AddScriptCapability(configuration);

        if (!services.Any(x => x.ServiceType == typeof(IWorkflowCatalogPort)))
            services.AddWorkflowCapability(configuration);

        services.AddOptions<GAgentServiceDemoOptions>()
            .Bind(configuration.GetSection("GAgentService:Demo"));
        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        services.AddGAgentServiceGovernanceCapability(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        services.TryAddSingleton<IServiceCommandTargetProvisioner, DefaultServiceCommandTargetProvisioner>();
        services.TryAddSingleton<IServiceRevisionArtifactStore, InMemoryServiceRevisionArtifactStore>();
        services.TryAddSingleton<IServiceRuntimeActivator, DefaultServiceRuntimeActivator>();
        services.TryAddSingleton<IServiceInvocationDispatcher, DefaultServiceInvocationDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, StaticServiceImplementationAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, ScriptingServiceImplementationAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServiceImplementationAdapter, WorkflowServiceImplementationAdapter>());
        services.TryAddSingleton<ServiceInvocationResolutionService>();
        services.TryAddSingleton<IServiceCommandPort, ServiceCommandApplicationService>();
        services.TryAddSingleton<IServiceLifecycleQueryPort, ServiceLifecycleQueryApplicationService>();
        services.TryAddSingleton<IServiceServingQueryPort, ServiceServingQueryApplicationService>();
        services.TryAddSingleton<IServiceInvocationPort, ServiceInvocationApplicationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GAgentServiceDemoBootstrapHostedService>());
        return services;
    }

    public static IServiceCollection AddGAgentServiceProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(GAgentServiceProjectionProviderRegistrationsMarker)))
            return services;

        services.AddSingleton<GAgentServiceProjectionProviderRegistrationsMarker>();
        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for GAgentService.");
        }

        if (elasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<ServiceCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceRevisionCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceDeploymentCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceServingSetReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceRolloutReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceTrafficViewReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ServiceCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceRevisionCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceDeploymentCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceServingSetReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceRolloutReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceTrafficViewReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                defaultSortSelector: readModel => readModel.UpdatedAt);
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

    private sealed class GAgentServiceProjectionProviderRegistrationsMarker;
}
