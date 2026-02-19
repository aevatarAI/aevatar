using Aevatar.Platform.Application.Abstractions.Ports;
using Aevatar.Platform.Sagas.Queries;
using Aevatar.Platform.Sagas.Tracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Platform.Sagas.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformCommandSagas(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISaga, PlatformCommandSaga>());
        services.TryAddSingleton<IPlatformCommandSagaTracker, PlatformCommandSagaTracker>();
        services.TryAddSingleton<IPlatformCommandSagaQueryService, PlatformCommandSagaQueryService>();
        return services;
    }
}
