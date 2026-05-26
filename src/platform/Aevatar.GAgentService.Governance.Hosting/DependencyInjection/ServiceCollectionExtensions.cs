using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Application.Services;
using Aevatar.GAgentService.Governance.Hosting.Identity;
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
        // Refactor (iter23/cluster-003-governance-legacy-startup-eventstore-fold):
        //   Old pattern: host startup replayed legacy event streams and folded state in process.
        //   New principle: normal startup composes ports only; migrations are explicit operator jobs.
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
        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "GAgentService governance");

        if (documentProvider.ElasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<ServiceConfigurationReadModel, string>(
                optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
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

}
