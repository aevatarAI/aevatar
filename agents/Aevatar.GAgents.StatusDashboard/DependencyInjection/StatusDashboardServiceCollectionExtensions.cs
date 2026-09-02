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
    /// Registers the status dashboard actor, operational snapshot query port,
    /// default executors, and startup service,
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
        services.TryAddSingleton<IHealthProbeOperationalSnapshotStore, InMemoryHealthProbeOperationalSnapshotStore>();

        services.TryAddSingleton<IHealthStatusQueryPort, HealthStatusQueryPort>();
        services.AddHostedService<HealthProbeStartupService>();

        return services;
    }
}
