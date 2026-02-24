using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectionProviderCapabilityValidator, ProjectionProviderCapabilityValidatorService>();
        services.TryAddSingleton<IProjectionDocumentStoreProviderRegistry, ProjectionDocumentStoreProviderRegistry>();
        services.TryAddSingleton<IProjectionDocumentStoreProviderSelector, ProjectionDocumentStoreProviderSelector>();
        services.TryAddSingleton<IProjectionStoreSelectionPlanner, ProjectionStoreSelectionPlanner>();
        services.TryAddSingleton<IProjectionDocumentStoreFactory, ProjectionDocumentStoreFactory>();
        services.TryAddSingleton<IProjectionGraphStoreProviderRegistry, ProjectionGraphStoreProviderRegistry>();
        services.TryAddSingleton<IProjectionGraphStoreProviderSelector, ProjectionGraphStoreProviderSelector>();
        services.TryAddSingleton<IProjectionGraphStoreFactory, ProjectionGraphStoreFactory>();
        services.TryAddSingleton(typeof(IProjectionGraphMaterializer<>), typeof(ProjectionGraphMaterializer<>));
        services.TryAddSingleton(typeof(IProjectionMaterializationRouter<,>), typeof(ProjectionMaterializationRouter<,>));
        services.TryAddSingleton<IProjectionDocumentMetadataResolver, ProjectionDocumentMetadataResolver>();
        services.TryAddSingleton<IProjectionStoreStartupValidator, ProjectionStoreStartupValidator>();
        return services;
    }
}
