using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能工具的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Ornn 技能工具系统。ornn_search_skills 工具始终注册到 LLM；远程技能按需获取通过
    /// IRemoteSkillFetcher 集成到统一的 use_skill 工具。所有 Ornn API 调用通过 NyxID 的
    /// proxy 路由，因此调用方必须先注册 NyxIdApiClient（一般通过 AddNyxIdTools）。
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
