using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.Executors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.StatusDashboard.DependencyInjection;

public static class StatusDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers the status dashboard projection pipeline (per-target actor,
    /// current-state projector, query port, default executors, startup service),
    /// and binds the probe manifest from the <c>Aevatar:Status</c> configuration
    /// section.
    /// </summary>
    public static IServiceCollection AddStatusDashboard(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Manifest binding
        services.AddOptions<StatusDashboardOptions>()
            .Bind(configuration.GetSection(StatusDashboardOptions.SectionName));
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(HealthProbeTargetGAgent).Assembly));

        // Default executors — additional executors / freshness sources can be
        // registered with TryAddEnumerable by other modules without touching
        // this extension.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient();
        services.TryAddSingleton<StatusProbeAuthorizationResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbeExecutor, HttpStatusProbeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProbeExecutor, ReadmodelFreshnessProbeExecutor>());
        services.TryAddSingleton<IHealthProbeExecutorRegistry, HealthProbeExecutorRegistry>();

        // Projection pipeline (per-actor current-state replica)
        services.AddProjectionMaterializationRuntimeCore<
            HealthProbeMaterializationContext,
            HealthProbeMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<HealthProbeMaterializationContext>>(
            static scopeKey => new HealthProbeMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new HealthProbeMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            HealthProbeMaterializationContext,
            HealthProbeTargetProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<HealthProbeTargetDocument>,
            HealthProbeTargetDocumentMetadataProvider>();
        services.TryAddSingleton<IHealthStatusQueryPort, HealthStatusQueryPort>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            HealthProbeCommittedStateProjectionActivationPlanProvider>());
        services.AddHostedService<HealthProbeStartupService>();

        return services;
    }
}
