using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.ChatRouting.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatRoutingCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ChatRoutingOptions>();
        services.TryAddSingleton<IChatRouteFallbackProvider, EnvChatRouteFallbackProvider>();
        services.TryAddSingleton<ChatRouteResolver>();
        services.TryAddSingleton<IChatRoutePolicyQueryPort, ChatRoutePolicyQueryPort>();

        return services;
    }
}
