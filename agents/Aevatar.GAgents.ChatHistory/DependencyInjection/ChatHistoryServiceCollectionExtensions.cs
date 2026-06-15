using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChatHistory.DependencyInjection;

public static class ChatHistoryServiceCollectionExtensions
{
    public static IServiceCollection AddChatHistoryGAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IChatHistoryIndexTopologyPort, DefaultChatHistoryIndexTopologyPort>();
        return services;
    }
}
