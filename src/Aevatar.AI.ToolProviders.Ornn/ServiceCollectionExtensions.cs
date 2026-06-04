using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>DI registration for Ornn skill tools.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Ornn skill tools. Ornn API calls route through NyxID's proxy; hosts should
    /// register NyxID tools when they need a configured NyxID authority.
    /// </summary>
    /// <remarks>
    /// The fallback concrete client keeps full-host DI validation self-contained without adding
    /// default NyxID options. If a host calls AddNyxIdTools later, its typed client registration
    /// remains the final concrete NyxIdApiClient registration.
    /// </remarks>
    public static IServiceCollection AddOrnnSkills(
        this IServiceCollection services,
        Action<OrnnOptions>? configure = null)
    {
        var options = new OrnnOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new NyxIdApiClient(
            sp.GetService<NyxIdToolOptions>() ?? new NyxIdToolOptions(),
            logger: sp.GetService<ILogger<NyxIdApiClient>>()));
        services.TryAddSingleton(sp =>
        {
            var nyxIdApiClient = sp.GetService<NyxIdApiClient>()
                                 ?? throw new InvalidOperationException(
                                     "AddOrnnSkills requires NyxIdApiClient. Call AddNyxIdTools before building the provider, or register NyxIdApiClient explicitly.");
            return new OrnnSkillClient(
                sp.GetRequiredService<OrnnOptions>(),
                nyxIdApiClient,
                sp.GetRequiredService<OrnnOptions>().PerCallTimeout,
                sp.GetService<ILogger<OrnnSkillClient>>());
        });
        services.TryAddSingleton<OrnnSkillPublishValidationPipeline>();
        services.TryAddSingleton<OrnnSkillPackageBuilder>();
        services.TryAddSingleton<OrnnSkillPackageFormatValidator>();
        services.TryAddSingleton<OrnnPublishSkillTool>();
        services.TryAddSingleton<IRemoteSkillFetcher, OrnnRemoteSkillFetcher>();
        services.TryAddSingleton<OrnnAgentToolSource>();
        services.TryAddAgentToolSourceAlias<OrnnAgentToolSource>(GetOrnnAgentToolSource);
        return services;
    }

    private static IAgentToolSource GetOrnnAgentToolSource(IServiceProvider sp) =>
        sp.GetRequiredService<OrnnAgentToolSource>();

    private static void TryAddAgentToolSourceAlias<TSource>(
        this IServiceCollection services,
        Func<IServiceProvider, IAgentToolSource> factory)
        where TSource : class, IAgentToolSource
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IAgentToolSource) &&
                (descriptor.ImplementationType == typeof(TSource) ||
                 descriptor.ImplementationInstance is TSource ||
                 descriptor.ImplementationFactory?.Method == factory.Method)))
        {
            return;
        }

        services.Add(ServiceDescriptor.Singleton(factory));
    }
}
