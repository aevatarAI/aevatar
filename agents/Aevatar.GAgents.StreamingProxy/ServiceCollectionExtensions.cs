using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.StreamingProxy;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStreamingProxy(this IServiceCollection services)
    {
        return services;
    }
}
