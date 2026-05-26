using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>DI registration extensions for Ornn skill tools.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ornn skill tool system. The <c>ornn_search_skills</c> tool is always
    /// advertised to the LLM; remote skill loading is integrated into the unified
    /// <c>use_skill</c> tool through <see cref="IRemoteSkillFetcher"/>. All Ornn API calls
    /// route through the NyxID proxy, so callers must register <see cref="NyxIdApiClient"/>
    /// first, usually through <c>AddNyxIdTools</c>.
    /// </summary>
    /// <remarks>
    /// We intentionally do NOT TryAdd a placeholder <c>NyxIdToolOptions</c>/<c>NyxIdApiClient</c>
    /// here as a "safety net". Doing so would shadow the real registration when call order is
    /// reversed: <c>AddAevatarAIFeatures</c> runs <c>RegisterOrnnSkills</c> early, and the
    /// host's <c>AddNyxIdTools</c> (which carries the configured BaseUrl) lands afterwards —
    /// since both use <c>TryAddSingleton</c>, the empty default would win and every NyxID call
    /// would fail at runtime with "NyxID base URL is not configured" (production incident
    /// 2026-05-08 caught the regression). Hosts that enable Ornn skills MUST call
    /// <c>AddNyxIdTools</c>; if they don't, DI resolution fails fast at startup, which is the
    /// signal we want.
    /// </remarks>
    public static IServiceCollection AddOrnnSkills(
        this IServiceCollection services,
        Action<OrnnOptions>? configure = null)
    {
        var options = new OrnnOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(sp =>
        {
            var nyxIdApiClient = sp.GetService<NyxIdApiClient>()
                                 ?? throw new InvalidOperationException(
                                     "AddOrnnSkills requires NyxIdApiClient. Call AddNyxIdTools before building the provider, or register NyxIdApiClient explicitly.");
            return new OrnnSkillClient(
                sp.GetRequiredService<OrnnOptions>(),
                nyxIdApiClient,
                sp.GetService<ILogger<OrnnSkillClient>>());
        });
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
