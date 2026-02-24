using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(typeof(IProjectionStoreDispatcher<,>), typeof(ProjectionStoreDispatcher<,>));
        services.TryAddSingleton(typeof(IProjectionQueryableStoreBinding<,>), typeof(ProjectionDocumentStoreBinding<,>));
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IProjectionStoreBinding<,>), typeof(ProjectionDocumentStoreBinding<,>)));
        services.TryAddSingleton<IProjectionDocumentMetadataResolver, ProjectionDocumentMetadataResolver>();
        return services;
    }
}
