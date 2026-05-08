using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
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
    /// 为避免 Ornn 单独被启用时（例如 workflow host 走 AddAevatarPlatform 路径但未调用
    /// AddNyxIdTools）DI 解析失败，方法内部会自带 <see cref="NyxIdApiClient"/> 与
    /// <see cref="NyxIdToolOptions"/> 的 TryAdd safety net；如果上层已注册更完整的
    /// 实例，TryAddSingleton 会让上层版本胜出。
    /// </summary>
    public static IServiceCollection AddOrnnSkills(
        this IServiceCollection services,
        Action<OrnnOptions>? configure = null)
    {
        var options = new OrnnOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // Safety net: OrnnSkillClient depends on NyxIdApiClient + NyxIdToolOptions. If the host
        // forgot to call AddNyxIdTools, fall back to bare singletons so resolution doesn't
        // throw a confusing "no service for NyxIdApiClient" at runtime — the caller will still
        // see clean Ornn 404s when BaseUrl is unset, which is much easier to debug.
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();

        services.TryAddSingleton<OrnnSkillClient>();
        services.TryAddSingleton<IRemoteSkillFetcher, OrnnRemoteSkillFetcher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, OrnnAgentToolSource>());
        return services;
    }
}
