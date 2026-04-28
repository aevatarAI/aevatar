using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.ChannelAdmin;

/// <summary>
/// DI registration entry point for the channel-admin tool provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel-admin tool source so LLM turns can manage channel
    /// registrations (create, list, delete) through agent-facing tools.
    /// </summary>
    public static IServiceCollection AddChannelAdminTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, ChannelRegistrationToolSource>());

        return services;
    }
}
