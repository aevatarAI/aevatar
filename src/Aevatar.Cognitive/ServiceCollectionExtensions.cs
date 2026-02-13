using Aevatar.Cognitive.Connectors;
using Aevatar.Connectors;
using Aevatar.EventModules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Cognitive;

/// <summary>
/// DI helpers for Cognitive workflow features.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Cognitive defaults:
    /// - <see cref="CognitiveModuleFactory"/>
    /// - <see cref="IConnectorRegistry"/> (in-memory)
    /// </summary>
    public static IServiceCollection AddAevatarCognitive(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventModuleFactory, CognitiveModuleFactory>();
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        return services;
    }
}
