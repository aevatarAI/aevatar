using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(
        this IServiceCollection services,
        Action<NyxIdRelayOptions>? configureRelay = null)
    {
        // Force-load the assembly so StaticServiceImplementationAdapter.ResolveType
        // can find NyxIdChatGAgent by type name string at service binding time.
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);

        services.TryAddSingleton<NyxIdChatActorStore>();

        if (configureRelay != null)
        {
            var options = new NyxIdRelayOptions();
            configureRelay(options);
            services.TryAddSingleton(options);
        }
        else
        {
            services.TryAddSingleton(new NyxIdRelayOptions());
        }

        return services;
    }
}
