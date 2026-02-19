using Aevatar.Maker.Core.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Maker.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarMakerCore(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventModuleFactory, MakerModuleFactory>());
        return services;
    }

    public static IServiceCollection AddMakerModuleFactory(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventModuleFactory, MakerModuleFactory>());
        return services;
    }
}
