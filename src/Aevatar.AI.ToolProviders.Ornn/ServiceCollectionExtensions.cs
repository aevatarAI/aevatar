using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能工具的 DI 注册扩展。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Ornn 技能工具系统。配置 BaseUrl 后，ornn_search_skills 自动注册，
    /// 远程技能获取通过 IRemoteSkillFetcher 集成到统一的 use_skill 工具。
    /// </summary>
    public static IServiceCollection AddOrnnSkills(
        this IServiceCollection services,
        Action<OrnnOptions> configure)
    {
        var options = new OrnnOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<OrnnSkillClient>();
        services.TryAddSingleton<IRemoteSkillFetcher, OrnnRemoteSkillFetcher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, OrnnAgentToolSource>());
        return services;
    }
}
