using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Extensions.Bridge;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowBridgeExtensions(this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkflowAgentTypeAliasProvider, TelegramBridgeAgentTypeAliasProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkflowAgentTypeAliasProvider, TelegramUserBridgeAgentTypeAliasProvider>());
        return services;
    }
}

internal sealed class TelegramBridgeAgentTypeAliasProvider : IWorkflowAgentTypeAliasProvider
{
    private static readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "telegram",
        "telegram_bridge",
        "telegram_bridge_gagent",
        nameof(TelegramBridgeGAgent),
        typeof(TelegramBridgeGAgent).Name,
        typeof(TelegramBridgeGAgent).FullName ?? string.Empty,
        typeof(TelegramBridgeGAgent).AssemblyQualifiedName ?? string.Empty,
    };

    public bool TryResolve(string alias, out Type agentType)
    {
        if (Aliases.Contains(alias.Trim()))
        {
            agentType = typeof(TelegramBridgeGAgent);
            return true;
        }

        agentType = typeof(TelegramBridgeGAgent);
        return false;
    }
}

internal sealed class TelegramUserBridgeAgentTypeAliasProvider : IWorkflowAgentTypeAliasProvider
{
    private static readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "telegram_user",
        "telegram_user_bridge",
        "telegram_user_bridge_gagent",
        nameof(TelegramUserBridgeGAgent),
        "TelegramUserBridigeGAgent",
        typeof(TelegramUserBridgeGAgent).Name,
        typeof(TelegramUserBridgeGAgent).FullName ?? string.Empty,
        typeof(TelegramUserBridgeGAgent).AssemblyQualifiedName ?? string.Empty,
    };

    public bool TryResolve(string alias, out Type agentType)
    {
        if (Aliases.Contains(alias.Trim()))
        {
            agentType = typeof(TelegramUserBridgeGAgent);
            return true;
        }

        agentType = typeof(TelegramUserBridgeGAgent);
        return false;
    }
}
