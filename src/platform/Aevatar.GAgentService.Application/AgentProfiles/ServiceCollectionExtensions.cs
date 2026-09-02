using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentProfileApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AgentProfileValidationLimits>();
        services.TryAddSingleton<AgentProfileSkillSealer>();
        services.TryAddSingleton<IAgentProfileSkillSealer>(static provider =>
            provider.GetRequiredService<AgentProfileSkillSealer>());
        return services;
    }
}
