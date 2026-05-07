using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能工具的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Ornn 技能工具系统。ornn_search_skills 工具始终注册到 LLM；远程技能按需获取通过
    /// IRemoteSkillFetcher 集成到统一的 use_skill 工具。所有 Ornn API 调用通过 NyxID 的
    /// proxy 路由，因此调用方必须先注册 NyxIdApiClient（一般通过 AddNyxIdTools）。
    /// </summary>
    public static IServiceCollection AddOrnnSkills(
        this IServiceCollection services,
        Action<OrnnOptions>? configure = null)
    {
        var options = new OrnnOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<OrnnSkillClient>();
        services.TryAddSingleton<IRemoteSkillFetcher, OrnnRemoteSkillFetcher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, OrnnAgentToolSource>());
        return services;
    }
}
