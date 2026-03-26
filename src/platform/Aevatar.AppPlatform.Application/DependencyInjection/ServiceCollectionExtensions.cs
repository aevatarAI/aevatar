using Aevatar.AppPlatform.Abstractions.Access;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AppPlatform.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppPlatformApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AppDefinitionQueryApplicationService>();
        services.TryAddSingleton<AppReleaseQueryApplicationService>();
        services.TryAddSingleton<AppRouteQueryApplicationService>();
        services.TryAddSingleton<IAppAccessAuthorizer, OwnerScopeAppAccessAuthorizer>();
        services.TryAddSingleton<IAppDefinitionQueryPort>(sp => sp.GetRequiredService<AppDefinitionQueryApplicationService>());
        services.TryAddSingleton<IAppReleaseQueryPort>(sp => sp.GetRequiredService<AppReleaseQueryApplicationService>());
        services.TryAddSingleton<IAppRouteQueryPort>(sp => sp.GetRequiredService<AppRouteQueryApplicationService>());
        return services;
    }
}
