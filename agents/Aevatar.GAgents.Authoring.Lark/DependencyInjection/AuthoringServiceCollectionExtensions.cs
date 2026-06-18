using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// DI registration entry point for the Lark-specific AgentBuilder authoring package.
/// </summary>
/// <remarks>
/// Both the package and the registration are intentionally Lark-specific (RFC §9.4 option a):
/// the AgentBuilder card flows are hard-coded to Lark `p2p` / `card_action` semantics.
/// Hosts that compose this package opt into Lark behavior by name; hosts targeting other
/// channels must not call <see cref="AddLarkAgentAuthoring"/>.
/// </remarks>
public static class AuthoringServiceCollectionExtensions
{
    private static readonly Func<IServiceProvider, object> AgentToolSourceFactory = CreateAgentBuilderToolSource;

    /// <summary>
    /// Registers the Lark-specific authoring, notification, and remote approval tools.
    /// </summary>
    public static IServiceCollection AddLarkAgentAuthoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<IChannelInteractionNotificationPort, FeishuCardNotificationPort>());
        services.Replace(ServiceDescriptor.Singleton<IRemoteToolApprovalNotificationPort, LarkRemoteToolApprovalNotificationPort>());
        services.TryAddSingleton<ScheduledAgentCreatorOptions>();
        services.TryAddSingleton<ScheduledAgentCreateRequestMapper>();
        services.TryAddSingleton<ScheduledAgentApiKeyIssuer>();
        services.TryAddSingleton<IScheduledAgentApiKeyIssuer>(sp => sp.GetRequiredService<ScheduledAgentApiKeyIssuer>());
        if (!services.Any(IsAgentBuilderToolSourceRegistration))
            services.Add(ServiceDescriptor.Singleton(typeof(IAgentToolSource), AgentToolSourceFactory));

        return services;
    }

    private static bool IsAgentBuilderToolSourceRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IAgentToolSource) &&
        (descriptor.ImplementationType == typeof(AgentBuilderToolSource) ||
         descriptor.ImplementationFactory == AgentToolSourceFactory);

    private static object CreateAgentBuilderToolSource(IServiceProvider sp) =>
        new AgentBuilderToolSource(
            sp.GetRequiredService<IUserAgentCatalogQueryPort>(),
            sp.GetRequiredService<ISkillRunnerExecutionQueryPort>(),
            sp.GetRequiredService<INyxIdApiClientFactory>(),
            sp.GetRequiredService<ISkillRunnerCommandPort>(),
            sp.GetRequiredService<IUserAgentCatalogCommandPort>(),
            sp.GetRequiredService<ICallerScopeResolver>(),
            sp.GetRequiredService<ScheduledAgentCreateRequestMapper>(),
            sp.GetRequiredService<ScheduledAgentApiKeyIssuer>(),
            sp.GetService<ILogger<AgentBuilderTool>>(),
            sp.GetService<ILogger<ScheduledAgentCreatorTool>>());
}
