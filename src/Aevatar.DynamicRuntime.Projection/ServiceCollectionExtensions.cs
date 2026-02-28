using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.DynamicRuntime.Projection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicRuntimeProjection(this IServiceCollection services)
    {
        services.AddProjectionReadModelRuntime();

        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeImageReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeStackReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeComposeServiceReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeComposeEventReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeServiceDefinitionReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeContainerReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeRunReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeBuildJobReadModel, string>(model => model.Id);
        services.AddInMemoryGraphProjectionStore();

        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeImageReadModel>, DynamicRuntimeImageReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeStackReadModel>, DynamicRuntimeStackReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeComposeServiceReadModel>, DynamicRuntimeComposeServiceReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeComposeEventReadModel>, DynamicRuntimeComposeEventReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeServiceDefinitionReadModel>, DynamicRuntimeServiceDefinitionReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeContainerReadModel>, DynamicRuntimeContainerReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeRunReadModel>, DynamicRuntimeRunReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeBuildJobReadModel>, DynamicRuntimeBuildJobReadModelMetadataProvider>();
        services.TryAddSingleton<IDynamicRuntimeReadStore, ProjectionBackedDynamicRuntimeReadStore>();

        return services;
    }
}
