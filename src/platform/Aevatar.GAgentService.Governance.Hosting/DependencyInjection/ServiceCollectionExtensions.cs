using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Application.Services;
using Aevatar.GAgentService.Governance.Infrastructure.Activation;
using Aevatar.GAgentService.Governance.Infrastructure.Admission;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Governance.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceGovernanceCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGAgentServiceGovernanceProjection();
        services.AddGAgentServiceGovernanceProjectionReadModelProviders(configuration);
        services.TryAddSingleton<IServiceGovernanceCommandTargetProvisioner, DefaultServiceGovernanceCommandTargetProvisioner>();
        services.TryAddSingleton<IActivationAdmissionEvaluator, DefaultActivationAdmissionEvaluator>();
        services.TryAddSingleton<IInvokeAdmissionEvaluator, DefaultInvokeAdmissionEvaluator>();
        services.TryAddSingleton<ActivationCapabilityViewAssembler>();
        services.TryAddSingleton<InvokeAdmissionService>();
        services.TryAddSingleton<IActivationCapabilityViewReader>(sp => sp.GetRequiredService<ActivationCapabilityViewAssembler>());
        services.TryAddSingleton<IInvokeAdmissionAuthorizer>(sp => sp.GetRequiredService<InvokeAdmissionService>());
        services.TryAddSingleton<IServiceGovernanceCommandPort, ServiceGovernanceCommandApplicationService>();
        services.TryAddSingleton<ServiceGovernanceQueryApplicationService>();
        services.TryAddSingleton<IServiceGovernanceQueryPort>(sp => sp.GetRequiredService<ServiceGovernanceQueryApplicationService>());
        return services;
    }

    public static IServiceCollection AddGAgentServiceGovernanceProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(GAgentServiceGovernanceProjectionProviderRegistrationsMarker)))
            return services;

        services.AddSingleton<GAgentServiceGovernanceProjectionProviderRegistrationsMarker>();
        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for GAgentService governance.");
        }

        if (elasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<ServiceBindingCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceBindingCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServiceEndpointCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceEndpointCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ServicePolicyCatalogReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServicePolicyCatalogReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ServiceBindingCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                listSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServiceEndpointCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                listSortSelector: readModel => readModel.UpdatedAt);
            services.AddInMemoryDocumentProjectionStore<ServicePolicyCatalogReadModel, string>(
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                listSortSelector: readModel => readModel.UpdatedAt);
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

    private sealed class GAgentServiceGovernanceProjectionProviderRegistrationsMarker;
}
