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
        services.TryAddSingleton<AppResourceQueryApplicationService>();
        services.TryAddSingleton<AppFunctionQueryApplicationService>();
        services.TryAddSingleton<AppFunctionExecutionTargetQueryApplicationService>();
        services.TryAddSingleton<AppFunctionInvocationApplicationService>();
        services.TryAddSingleton<AppControlCommandApplicationService>();
        services.TryAddSingleton<OperationQueryApplicationService>();
        services.TryAddSingleton<OperationCommandApplicationService>();
        services.TryAddSingleton<IAppAccessAuthorizer, OwnerScopeAppAccessAuthorizer>();
        services.TryAddSingleton<IAppDefinitionQueryPort>(sp => sp.GetRequiredService<AppDefinitionQueryApplicationService>());
        services.TryAddSingleton<IAppReleaseQueryPort>(sp => sp.GetRequiredService<AppReleaseQueryApplicationService>());
        services.TryAddSingleton<IAppRouteQueryPort>(sp => sp.GetRequiredService<AppRouteQueryApplicationService>());
        services.TryAddSingleton<IAppResourceQueryPort>(sp => sp.GetRequiredService<AppResourceQueryApplicationService>());
        services.TryAddSingleton<IAppFunctionQueryPort>(sp => sp.GetRequiredService<AppFunctionQueryApplicationService>());
        services.TryAddSingleton<IAppFunctionExecutionTargetQueryPort>(sp => sp.GetRequiredService<AppFunctionExecutionTargetQueryApplicationService>());
        services.TryAddSingleton<IAppFunctionInvocationPort>(sp => sp.GetRequiredService<AppFunctionInvocationApplicationService>());
        services.TryAddSingleton<IAppControlCommandPort>(sp => sp.GetRequiredService<AppControlCommandApplicationService>());
        services.TryAddSingleton<IOperationQueryPort>(sp => sp.GetRequiredService<OperationQueryApplicationService>());
        services.TryAddSingleton<IOperationCommandPort>(sp => sp.GetRequiredService<OperationCommandApplicationService>());
        return services;
    }
}
