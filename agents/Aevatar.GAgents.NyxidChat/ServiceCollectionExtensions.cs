using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services)
    {
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);

        services.TryAddSingleton<NyxIdChatActorStore>();
        services.TryAddSingleton(new NyxIdRelayOptions());

        return services;
    }
}
