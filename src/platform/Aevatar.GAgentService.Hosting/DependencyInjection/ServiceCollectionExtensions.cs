using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.GAgentService.Application.Workflows;
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
using Aevatar.Studio.Projection.ReadModels;
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

        if (!services.Any(x => x.ServiceType == typeof(Aevatar.Scripting.Hosting.DependencyInjection.ServiceCollectionExtensions.ScriptCapabilityRegistrationsMarker)))
            services.AddScriptCapability(configuration);

        if (!services.Any(x => x.ServiceType == typeof(WorkflowCapabilityServiceCollectionExtensions.WorkflowCapabilityRegistrationsMarker)))
            services.AddWorkflowCapability(configuration);

        services.AddOptions<GAgentServiceDemoOptions>()
            .Bind(configuration.GetSection("GAgentService:Demo"));
        services.AddGAgentServiceProjection();
        services.AddGAgentServiceProjectionReadModelProviders(configuration);
        services.AddGAgentServiceGovernanceCapability(configuration);
        services.TryAddSingleton<PreparedServiceRevisionArtifactAssembler>();
        services.TryAddSingleton<IServiceServingTargetResolver, DefaultServiceServingTargetResolver>();
        services.TryAddSingleton<IServiceCommandTargetProvisioner, DefaultServiceCommandTargetProvisioner>();
        services.TryAddSingleton<IServiceRevisionArtifactStore, ConfiguredServiceRevisionArtifactStore>();
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
        services.AddOptions<ScopeWorkflowCapabilityOptions>()
            .Bind(configuration.GetSection(ScopeWorkflowCapabilityOptions.SectionName));
        services.TryAddSingleton<ScopeWorkflowQueryApplicationService>();
        services.TryAddSingleton<IScopeWorkflowQueryPort>(sp => sp.GetRequiredService<ScopeWorkflowQueryApplicationService>());
        services.TryAddSingleton<IScopeWorkflowCommandPort, ScopeWorkflowCommandApplicationService>();
        services.TryAddSingleton<IScopeBindingCommandPort, ScopeBindingCommandApplicationService>();
        services.AddOptions<ScopeScriptCapabilityOptions>()
            .Bind(configuration.GetSection(ScopeScriptCapabilityOptions.SectionName));
        services.TryAddSingleton<ScopeScriptQueryApplicationService>();
        services.TryAddSingleton<IScopeScriptQueryPort>(sp => sp.GetRequiredService<ScopeScriptQueryApplicationService>());
        services.TryAddSingleton<IScopeScriptCommandPort, ScopeScriptCommandApplicationService>();
        services.TryAddSingleton<IScopeScriptSaveObservationPort, ScopeScriptSaveObservationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GAgentServiceDemoBootstrapHostedService>());
        return services;
    }

    public static IServiceCollection AddGAgentServiceProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (HasAllGAgentServiceProjectionReaders(services))
            return services;
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
            TryAddElasticsearchDocumentProjectionStore<ServiceCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRevisionCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceDeploymentCatalogReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceServingSetReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRolloutReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceRolloutCommandObservationReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<ServiceTrafficViewReadModel>(services, configuration, static readModel => readModel.Id);
            TryAddElasticsearchDocumentProjectionStore<UserConfigCurrentStateDocument>(services, configuration, static readModel => readModel.Id);
        }
        else
        {
            TryAddInMemoryDocumentProjectionStore<ServiceCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRevisionCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceDeploymentCatalogReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceServingSetReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRolloutReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceRolloutCommandObservationReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<ServiceTrafficViewReadModel>(services, static readModel => readModel.Id);
            TryAddInMemoryDocumentProjectionStore<UserConfigCurrentStateDocument>(services, static readModel => readModel.Id);
        }

        return services;
    }

    private static bool HasAllGAgentServiceProjectionReaders(IServiceCollection services)
    {
        return HasProjectionDocumentReader<ServiceCatalogReadModel>(services)
               && HasProjectionDocumentReader<ServiceRevisionCatalogReadModel>(services)
               && HasProjectionDocumentReader<ServiceDeploymentCatalogReadModel>(services)
               && HasProjectionDocumentReader<ServiceServingSetReadModel>(services)
               && HasProjectionDocumentReader<ServiceRolloutReadModel>(services)
               && HasProjectionDocumentReader<ServiceRolloutCommandObservationReadModel>(services)
               && HasProjectionDocumentReader<ServiceTrafficViewReadModel>(services)
               && HasProjectionDocumentReader<UserConfigCurrentStateDocument>(services);
    }

    private static bool HasProjectionDocumentReader<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TReadModel, string>));
    }

    private static void TryAddElasticsearchDocumentProjectionStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (HasProjectionDocumentReader<TReadModel>(services))
            return;

        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryDocumentProjectionStore<TReadModel>(
        IServiceCollection services,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (HasProjectionDocumentReader<TReadModel>(services))
            return;

        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
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
