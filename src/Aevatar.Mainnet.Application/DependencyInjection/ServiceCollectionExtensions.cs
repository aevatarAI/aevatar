using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Application.DependencyInjection;

public delegate IServiceCollection MainnetCapabilityRegistration(
    IServiceCollection services,
    IConfiguration configuration);

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMainnetCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }

    public static IServiceCollection AddMainnetCapability(
        this IServiceCollection services,
        IConfiguration configuration,
        MainnetCapabilityRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registration(services, configuration);
    }

    public static IServiceCollection AddMainnetCapabilities(
        this IServiceCollection services,
        IConfiguration configuration,
        params MainnetCapabilityRegistration[] registrations)
    {
        if (registrations is null)
            return services;

        foreach (var registration in registrations)
        {
            if (registration is null)
                continue;

            services = registration(services, configuration);
        }

        return services;
    }
}
