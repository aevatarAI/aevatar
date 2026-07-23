using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AgentCatalog.AgentProfiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

/// <summary>
/// DI registration entry point for the agent-catalog tool provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    private static readonly Func<IServiceProvider, object> DeliveryTargetToolSourceFactory =
        CreateDeliveryTargetToolSource;
    private static readonly Func<IServiceProvider, object> AgentProfilesToolSourceFactory =
        CreateAgentProfilesToolSource;

    /// <summary>
    /// Registers the agent-delivery-target tool source so LLM turns can resolve the
    /// catalog of user-owned agents available as delivery targets.
    /// </summary>
    public static IServiceCollection AddAgentCatalogTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(IsDeliveryTargetToolSourceRegistration))
        {
            services.Add(ServiceDescriptor.Singleton(
                typeof(IAgentToolSource),
                DeliveryTargetToolSourceFactory));
        }

        if (!services.Any(IsAgentProfilesToolSourceRegistration))
        {
            services.Add(ServiceDescriptor.Singleton(
                typeof(IAgentToolSource),
                AgentProfilesToolSourceFactory));
        }

        return services;
    }

    private static bool IsDeliveryTargetToolSourceRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IAgentToolSource) &&
        (descriptor.ImplementationType == typeof(AgentDeliveryTargetToolSource) ||
         descriptor.ImplementationFactory == DeliveryTargetToolSourceFactory);

    private static bool IsAgentProfilesToolSourceRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IAgentToolSource) &&
        (descriptor.ImplementationType == typeof(AgentProfilesToolSource) ||
         descriptor.ImplementationFactory == AgentProfilesToolSourceFactory);

    private static object CreateDeliveryTargetToolSource(IServiceProvider sp) =>
        new AgentDeliveryTargetToolSource(
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.IUserAgentCatalogQueryPort>(),
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.IUserAgentCatalogCommandPort>(),
            sp.GetRequiredService<Aevatar.GAgents.Scheduled.ICallerScopeResolver>(),
            sp.GetRequiredService<Aevatar.Foundation.Abstractions.Credentials.ISecretVault>(),
            sp.GetService<Aevatar.GAgents.Scheduled.IScheduledAgentApiKeyIssuer>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Schedules.Authorization.IScheduledInvocationAuthorizationPlanner>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Schedules.Authorization.IScheduledInvocationAuthorizationRevalidator>(),
            sp.GetService<Aevatar.GAgents.Scheduled.ScheduledAgentCreatorOptions>());

    private static object CreateAgentProfilesToolSource(IServiceProvider sp) =>
        new AgentProfilesToolSource(
            () => sp.GetRequiredService<Aevatar.GAgentService.Abstractions.AgentProfiles.IAgentProfileCommandService>(),
            () => sp.GetRequiredService<Aevatar.GAgentService.Abstractions.AgentProfiles.IAgentProfileQueryService>());
}
