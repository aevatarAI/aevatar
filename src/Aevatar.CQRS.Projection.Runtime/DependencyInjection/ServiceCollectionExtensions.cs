using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ProjectionRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(new ProjectionStoreDispatchOptions());
        services.TryAddSingleton(typeof(IProjectionStoreDispatchCompensator<,>), typeof(LoggingProjectionStoreDispatchCompensator<,>));
        services.TryAddSingleton(typeof(IProjectionStoreDispatcher<,>), typeof(ProjectionStoreDispatcher<,>));
        services.TryAddSingleton(typeof(IProjectionWriteDispatcher<,>), typeof(ProjectionStoreDispatcher<,>));
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IProjectionStoreBinding<,>), typeof(ProjectionDocumentStoreBinding<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IProjectionStoreBinding<,>), typeof(ProjectionGraphStoreBinding<,>)));
        services.TryAddSingleton<IProjectionDocumentMetadataResolver, ProjectionDocumentMetadataResolver>();
        return services;
    }
}
