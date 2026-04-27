using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Authoring;

/// <summary>
/// DI registration entry point for the agent-authoring (AgentBuilder) package.
/// </summary>
public static class AuthoringServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the default <see cref="IHumanInteractionPort"/> with the Feishu-card
    /// implementation and registers the AgentBuilder tool source so LLM turns can author
    /// new agents through interactive cards.
    /// </summary>
    public static IServiceCollection AddAgentAuthoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IHumanInteractionPort, FeishuCardHumanInteractionPort>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, AgentBuilderToolSource>());

        return services;
    }
}
