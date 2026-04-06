using Aevatar.GAgents.ChannelRuntime.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChannelRuntime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<ChannelBotRegistrationStore>();

        // Register platform adapters (add more as platforms are onboarded)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, LarkPlatformAdapter>());

        return services;
    }
}
