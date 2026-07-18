using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChatHistory.DependencyInjection;

public static class ChatHistoryServiceCollectionExtensions
{
    public static IServiceCollection AddChatHistoryGAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(ChatConversationGAgent).Assembly));
        services.TryAddSingleton<IWorkflowChatHistoryTerminalDeliveryPort, ChatTurnHistoryTerminalDeliveryPort>();
        return services;
    }
}
