using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
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
    public static IServiceCollection AddGAgentServiceProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddServiceProjectionRuntime<ServiceCatalogProjectionContext, ServiceCatalogProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });
        services.AddServiceProjectionRuntime<ServiceDeploymentCatalogProjectionContext, ServiceDeploymentCatalogProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });
        services.AddServiceProjectionRuntime<ServiceRevisionCatalogProjectionContext, ServiceRevisionCatalogProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });
        services.AddServiceProjectionRuntime<ServiceServingSetProjectionContext, ServiceServingSetProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceServingSetProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceServingSetProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });
        services.AddServiceProjectionRuntime<ServiceRolloutProjectionContext, ServiceRolloutProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceRolloutProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceRolloutProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });
        services.AddServiceProjectionRuntime<ServiceTrafficViewProjectionContext, ServiceTrafficViewProjectionScopeGAgent>(
            static (rootActorId, projectionName) => new ServiceTrafficViewProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static (_, context) => new ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>(context.RootActorId, context),
            static lease => new ProjectionRuntimeScopeKey(
                lease.Context.RootActorId,
                lease.Context.ProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization),
            static scopeKey => new ServiceTrafficViewProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            });

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
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>, ServiceTrafficViewReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceDeploymentCatalogQueryReader, ServiceDeploymentCatalogQueryReader>();
        services.TryAddSingleton<IServiceServingSetQueryReader, ServiceServingSetQueryReader>();
        services.TryAddSingleton<IServiceRolloutQueryReader, ServiceRolloutQueryReader>();
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
        Func<string, string, TContext> contextFactory,
        Func<ProjectionRuntimeScopeKey, TContext, ServiceProjectionRuntimeLease<TContext>> leaseFactory,
        Func<ServiceProjectionRuntimeLease<TContext>, ProjectionRuntimeScopeKey> scopeKeyAccessor,
        Func<ProjectionRuntimeScopeKey, TContext> scopeContextFactory)
        where TContext : class, IProjectionMaterializationContext
        where TScopeAgent : IAgent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(leaseFactory);
        ArgumentNullException.ThrowIfNull(scopeKeyAccessor);
        ArgumentNullException.ThrowIfNull(scopeContextFactory);

        services.AddProjectionMaterializationRuntimeCore<
            TContext,
            ServiceProjectionRuntimeLease<TContext>>();
        services.TryAddSingleton<IProjectionScopeContextFactory<TContext>>(
            _ => new ProjectionScopeContextFactory<TContext>(scopeContextFactory));
        services.TryAddSingleton<IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<TContext>>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                ServiceProjectionRuntimeLease<TContext>,
                TContext,
                TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => contextFactory(request.RootActorId, request.ProjectionKind),
                leaseFactory,
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ServiceProjectionRuntimeLease<TContext>>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                ServiceProjectionRuntimeLease<TContext>,
                TScopeAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                scopeKeyAccessor,
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));

        return services;
    }
}
