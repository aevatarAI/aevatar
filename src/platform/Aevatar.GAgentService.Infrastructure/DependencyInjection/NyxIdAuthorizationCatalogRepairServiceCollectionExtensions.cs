using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Infrastructure.DependencyInjection;

public static class NyxIdAuthorizationCatalogRepairServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdAuthorizationCatalogVersionRegressionRepairPorts(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<
            INyxIdAuthorizationCatalogRepairCommandPort,
            NyxIdAuthorizationCatalogRepairCommandPort>();
        services.TryAddSingleton<
            INyxIdAuthorizationCatalogRepairRefreshPort,
            NyxIdAuthorizationCatalogRepairRefreshPort>();
        return services;
    }
}
