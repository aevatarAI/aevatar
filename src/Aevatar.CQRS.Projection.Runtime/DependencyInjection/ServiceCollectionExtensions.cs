using Aevatar.CQRS.Projection.Runtime.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Runtime.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionReadModelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(typeof(IDocumentProjectionStore<,>), typeof(ProjectionDocumentStoreFanout<,>));
        services.TryAddSingleton<IProjectionGraphStore, ProjectionGraphStoreFanout>();
        services.TryAddSingleton(typeof(IProjectionGraphMaterializer<>), typeof(ProjectionGraphMaterializer<>));
        services.TryAddSingleton(typeof(IProjectionMaterializationRouter<,>), typeof(ProjectionMaterializationRouter<,>));
        services.TryAddSingleton<IProjectionDocumentMetadataResolver, ProjectionDocumentMetadataResolver>();
        return services;
    }
}
