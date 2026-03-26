using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Infrastructure.Configuration;
using Aevatar.AppPlatform.Infrastructure.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AppPlatform.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppPlatformInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AppPlatformOptions>()
            .Bind(configuration.GetSection(AppPlatformOptions.SectionName));
        services.TryAddSingleton<InMemoryAppRegistryStore>();
        services.TryAddSingleton<IAppOperationStore, InMemoryOperationStore>();
        services.TryAddSingleton<IAppRegistryReader>(sp => sp.GetRequiredService<InMemoryAppRegistryStore>());
        services.TryAddSingleton<IAppRegistryWriter>(sp => sp.GetRequiredService<InMemoryAppRegistryStore>());
        return services;
    }
}
