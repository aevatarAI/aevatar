using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceProjection(
        this IServiceCollection services,
        Action<ServiceProjectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ServiceProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddServiceProjectionRuntime<ServiceCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceCatalogProjectionContext>>(
            static scopeKey => new ServiceCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceDeploymentCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceDeploymentCatalogProjectionContext>>(
            static scopeKey => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceRevisionCatalogProjectionContext, ProjectionMaterializationScopeGAgent<ServiceRevisionCatalogProjectionContext>>(
            static scopeKey => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceServingSetProjectionContext, ProjectionMaterializationScopeGAgent<ServiceServingSetProjectionContext>>(
            static scopeKey => new ServiceServingSetProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceRolloutProjectionContext, ProjectionMaterializationScopeGAgent<ServiceRolloutProjectionContext>>(
            static scopeKey => new ServiceRolloutProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>(context.RootActorId, context));
        services.AddServiceProjectionRuntime<ServiceTrafficViewProjectionContext, ProjectionMaterializationScopeGAgent<ServiceTrafficViewProjectionContext>>(
            static scopeKey => new ServiceTrafficViewProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>(context.RootActorId, context));

        services.TryAddSingleton<IServiceCatalogProjectionPort, ServiceCatalogProjectionPort>();
        services.TryAddSingleton<IServiceDeploymentCatalogProjectionPort, ServiceDeploymentCatalogProjectionPort>();
        services.TryAddSingleton<IServiceServingSetProjectionPort, ServiceServingSetProjectionPort>();
        services.TryAddSingleton<IServiceRolloutProjectionPort, ServiceRolloutProjectionPort>();
        services.TryAddSingleton<IServiceTrafficViewProjectionPort, ServiceTrafficViewProjectionPort>();
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort, ServiceRevisionCatalogProjectionPort>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>, ServiceCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>, ServiceDeploymentCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>, ServiceServingSetReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>, ServiceRolloutReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutCommandObservationReadModel>, ServiceRolloutCommandObservationReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>, ServiceTrafficViewReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceDeploymentCatalogQueryReader, ServiceDeploymentCatalogQueryReader>();
        services.TryAddSingleton<IServiceServingSetQueryReader, ServiceServingSetQueryReader>();
        services.TryAddSingleton<IServiceRolloutQueryReader, ServiceRolloutQueryReader>();
        services.TryAddSingleton<IServiceRolloutCommandObservationQueryReader, ServiceRolloutCommandObservationQueryReader>();
        services.TryAddSingleton<IServiceTrafficViewQueryReader, ServiceTrafficViewQueryReader>();
        services.TryAddSingleton<IServiceRevisionCatalogQueryReader, ServiceRevisionCatalogQueryReader>();
        services.AddProjectionArtifactMaterializer<
            ServiceCatalogProjectionContext,
            ServiceCatalogProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceDeploymentCatalogProjectionContext,
            ServiceDeploymentCatalogProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ServiceServingSetProjectionContext,
            ServiceServingSetProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRolloutProjectionContext,
            ServiceRolloutProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRolloutProjectionContext,
            ServiceRolloutCommandObservationProjector>();
        services.AddCurrentStateProjectionMaterializer<
            ServiceTrafficViewProjectionContext,
            ServiceTrafficViewProjector>();
        services.AddProjectionArtifactMaterializer<
            ServiceRevisionCatalogProjectionContext,
            ServiceRevisionCatalogProjector>();

        return services;
    }

    private static IServiceCollection AddServiceProjectionRuntime<TContext, TScopeAgent>(
        this IServiceCollection services,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory,
        Func<TContext, ServiceProjectionRuntimeLease<TContext>> leaseFactory)
        where TContext : class, IProjectionMaterializationContext
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);

        services.AddProjectionMaterializationRuntimeCore<
            TContext,
            ServiceProjectionRuntimeLease<TContext>,
            TScopeAgent>(
            contextFactory,
            leaseFactory);
        return services;
    }
}
