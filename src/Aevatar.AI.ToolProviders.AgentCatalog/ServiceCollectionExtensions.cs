using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

/// <summary>
/// DI registration entry point for the agent-catalog tool provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Func<IServiceProvider, object> ToolSourceFactory = CreateToolSource;

    /// <summary>
    /// Registers the agent-delivery-target tool source so LLM turns can resolve the
    /// catalog of user-owned agents available as delivery targets.
    /// </summary>
    public static IServiceCollection AddAgentCatalogTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(IsToolSourceRegistration))
            services.Add(ServiceDescriptor.Singleton(typeof(IAgentToolSource), ToolSourceFactory));

        return services;
    }

    private static bool IsToolSourceRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IAgentToolSource) &&
        (descriptor.ImplementationType == typeof(AgentDeliveryTargetToolSource) ||
         descriptor.ImplementationFactory == ToolSourceFactory);

    private static object CreateToolSource(IServiceProvider sp) =>
        new AgentDeliveryTargetToolSource(
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.IUserAgentCatalogQueryPort>(),
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.IUserAgentCatalogCommandPort>(),
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.ICallerScopeResolver>(),
            sp.GetRequiredService<Aevatar.Foundation.Abstractions.Credentials.ISecretVault>(),
            sp.GetService<Aevatar.GAgents.Scheduled.IScheduledAgentApiKeyIssuer>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Schedules.Authorization.IScheduledInvocationAuthorizationPlanner>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Schedules.Authorization.IScheduledInvocationAuthorizationRevalidator>(),
            sp.GetService<Aevatar.GAgents.Scheduled.ScheduledAgentCreatorOptions>());
}
