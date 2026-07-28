using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Application.Schedules.Authorization;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScheduledInvocationAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IScheduledInvocationAuthorizationPlanner, ScheduledInvocationAuthorizationPlanner>();
        services.TryAddSingleton<IScheduledInvocationAuthorizationRevalidator, ScheduledInvocationAuthorizationRevalidator>();
        services.TryAddSingleton<
            INyxIdAuthorizationCatalogVisibilityPort,
            NyxIdAuthorizationCatalogVisibilityService>();
        return services;
    }
}
