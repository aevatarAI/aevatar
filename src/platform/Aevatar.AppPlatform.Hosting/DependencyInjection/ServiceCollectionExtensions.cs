using Aevatar.AppPlatform.Application.DependencyInjection;
using Aevatar.AppPlatform.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AppPlatform.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppPlatformCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAppPlatformInfrastructure(configuration);
        services.AddAppPlatformApplication();
        return services;
    }
}
