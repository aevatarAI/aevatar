using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services)
    {
        services.TryAddSingleton<NyxIdChatActorStore>();
        return services;
    }
}
