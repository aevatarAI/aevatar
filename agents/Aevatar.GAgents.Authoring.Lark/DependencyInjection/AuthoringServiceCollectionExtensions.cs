using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// DI registration entry point for the Lark-specific AgentBuilder authoring package.
/// </summary>
/// <remarks>
/// Both the package and the registration are intentionally Lark-specific (RFC §9.4 option a):
/// <see cref="FeishuCardHumanInteractionPort"/> is the Lark interactive-card implementation of
/// <see cref="IHumanInteractionPort"/>, and the AgentBuilder card flows are hard-coded to Lark
/// `p2p` / `card_action` semantics. Hosts that compose this package opt into Lark behavior by
/// name; hosts targeting other channels must not call <see cref="AddLarkAgentAuthoring"/>.
/// </remarks>
public static class AuthoringServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the default <see cref="IHumanInteractionPort"/> with the Feishu-card
    /// implementation and registers the AgentBuilder tool source so LLM turns can author
    /// new agents through interactive Lark cards.
    /// </summary>
    public static IServiceCollection AddLarkAgentAuthoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IHumanInteractionPort, FeishuCardHumanInteractionPort>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, AgentBuilderToolSource>());

        return services;
    }
}
