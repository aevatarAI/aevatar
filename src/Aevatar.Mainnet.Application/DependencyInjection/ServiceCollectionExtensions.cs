using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Mainnet.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMainnetCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _ = configuration;
        return services;
    }
}
