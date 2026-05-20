using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Application.Services;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Aevatar.GAgentService.Governance.Infrastructure.Activation;
using Aevatar.GAgentService.Governance.Infrastructure.Admission;
using Aevatar.GAgentService.Governance.Hosting.Migration;
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
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IServiceIdentityContextResolver, DefaultServiceIdentityContextResolver>();
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
#pragma warning disable CS0618 // Legacy migration — remove after all deployments complete governance migration
        services.AddHostedService<ServiceGovernanceLegacyMigrationHostedService>();
#pragma warning restore CS0618
        return services;
    }

    public static IServiceCollection AddGAgentServiceGovernanceProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<ServiceConfigurationReadModel, string>)))
            return services;
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
            services.AddElasticsearchDocumentProjectionStore<ServiceConfigurationReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<ServiceConfigurationReadModel, string>(
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
}
