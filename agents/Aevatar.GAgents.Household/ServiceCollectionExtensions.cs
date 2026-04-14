using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Household;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers HouseholdEntity as an agent tool that can be discovered
    /// and called by any AIGAgentBase (e.g., NyxIdChatGAgent).
    /// </summary>
    public static IServiceCollection AddHouseholdEntityTools(
        this IServiceCollection services,
        Action<HouseholdEntityToolOptions>? configure = null)
    {
        var options = new HouseholdEntityToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, HouseholdEntityToolSource>());

        return services;
    }
}
