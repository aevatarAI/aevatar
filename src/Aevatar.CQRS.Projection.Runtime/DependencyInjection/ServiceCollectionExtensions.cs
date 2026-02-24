using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<IProjectionReadModelCapabilityValidator, ProjectionReadModelCapabilityValidatorService>();
        services.TryAddSingleton<IProjectionReadModelProviderRegistry, ProjectionReadModelProviderRegistry>();
        services.TryAddSingleton<IProjectionReadModelProviderSelector, ProjectionReadModelProviderSelector>();
        services.TryAddSingleton<IProjectionReadModelBindingResolver, ProjectionReadModelBindingResolver>();
        services.TryAddSingleton<IProjectionReadModelStoreFactory, ProjectionReadModelStoreFactory>();
        services.TryAddSingleton<IProjectionRelationStoreProviderRegistry, ProjectionRelationStoreProviderRegistry>();
        services.TryAddSingleton<IProjectionRelationStoreProviderSelector, ProjectionRelationStoreProviderSelector>();
        services.TryAddSingleton<IProjectionRelationStoreFactory, ProjectionRelationStoreFactory>();
        return services;
    }
}
