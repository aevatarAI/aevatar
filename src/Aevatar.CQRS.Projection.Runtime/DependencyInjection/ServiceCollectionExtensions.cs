using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ProjectionRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(new ProjectionStoreDispatchOptions());
        services.TryAddSingleton(typeof(IProjectionStoreDispatchCompensator<>), typeof(LoggingProjectionStoreDispatchCompensator<>));
        services.TryAddSingleton(typeof(IProjectionWriteDispatcher<>), typeof(ProjectionStoreDispatcher<>));
        services.TryAddSingleton(typeof(IProjectionGraphWriter<>), typeof(ProjectionGraphWriter<>));
        services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IProjectionWriteSink<>), typeof(ProjectionDocumentStoreBinding<>)));
        return services;
    }
}
