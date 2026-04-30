using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Core.TypeSystem;

public static class AgentKindRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default <see cref="IAgentKindRegistry"/> and the
    /// transitional <see cref="ILegacyAgentClrTypeResolver"/> reflection
    /// fallback. Modules contribute kinds via <paramref name="configure"/>;
    /// repeated calls accumulate into the same shared builder so each
    /// module's DI extension can scan its own assembly without races.
    /// </summary>
    public static IServiceCollection AddAevatarAgentKindRegistry(
        this IServiceCollection services,
        Action<AgentKindRegistryBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = ResolveSharedBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton<ILegacyAgentClrTypeResolver, ReflectionLegacyAgentClrTypeResolver>();
        return services;
    }

    private static AgentKindRegistryBuilder ResolveSharedBuilder(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(static descriptor =>
            descriptor.ServiceType == typeof(AgentKindRegistryBuilder));

        if (existing?.ImplementationInstance is AgentKindRegistryBuilder shared)
            return shared;

        var builder = new AgentKindRegistryBuilder();
        services.AddSingleton(builder);
        // First call wins for the registry factory: subsequent
        // AddAevatarAgentKindRegistry(...) invocations only mutate the shared
        // builder, never re-register the factory, so DI ordering is stable.
        services.TryAddSingleton<IAgentKindRegistry>(sp =>
            new AgentKindRegistry(sp.GetRequiredService<AgentKindRegistryBuilder>().Build()));
        return builder;
    }
}
