using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.Metadata;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Projectors;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Governance.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceGovernanceProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddProjectionMaterializationRuntimeCore<
            ServiceConfigurationProjectionContext,
            ServiceConfigurationRuntimeLease>();
        services.TryAddSingleton<IProjectionScopeContextFactory<ServiceConfigurationProjectionContext>>(
            _ => new ProjectionScopeContextFactory<ServiceConfigurationProjectionContext>(scopeKey =>
                new ServiceConfigurationProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                }));

        services.TryAddSingleton<IProjectionMaterializationActivationService<ServiceConfigurationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeActivationService<
                ServiceConfigurationRuntimeLease,
                ServiceConfigurationProjectionContext,
                ServiceConfigurationProjectionScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                static request => new ServiceConfigurationProjectionContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                },
                static (_, context) => new ServiceConfigurationRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ServiceConfigurationRuntimeLease>>(sp =>
            new ProjectionMaterializationScopeReleaseService<
                ServiceConfigurationRuntimeLease,
                ServiceConfigurationProjectionScopeGAgent>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentTypeVerifier>()));
        services.TryAddSingleton<ServiceConfigurationProjectionPort>();
        services.TryAddSingleton<IServiceConfigurationProjectionPort>(sp => sp.GetRequiredService<ServiceConfigurationProjectionPort>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>, ServiceConfigurationReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceConfigurationQueryReader, ServiceConfigurationQueryReader>();
        services.AddProjectionArtifactMaterializer<
            ServiceConfigurationProjectionContext,
            ServiceConfigurationProjector>();

        return services;
    }
}
