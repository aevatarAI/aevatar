using Aevatar.Foundation.Runtime.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarActorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarActorRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AevatarActorRuntimeOptions();
        var configuredProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            options.Provider = configuredProvider;
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarRuntime();
            return services;
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
    }
}
