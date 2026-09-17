// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — Skills DI 注册
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>Skills 选项。</summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class SkillsOptions
{
    /// <summary>技能扫描目录列表。</summary>
    public List<string> Directories { get; } = [];

    /// <summary>添加扫描目录。</summary>
    public SkillsOptions ScanDirectory(string directory)
    {
        Directories.Add(directory);
        return this;
    }
}

/// <summary>Skills 系统的 DI 注册扩展。</summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Skills 系统。配置扫描目录后，技能自动发现并注册。
    /// </summary>
    /// <example>
    /// services.AddSkills(o => o
    ///     .ScanDirectory("~/.aevatar/skills")
    ///     .ScanDirectory("./skills"));
    /// </example>
    public static IServiceCollection AddSkills(
        this IServiceCollection services,
        Action<SkillsOptions> configure)
    {
        var options = new SkillsOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<SkillFrontmatterParser>();
        services.TryAddSingleton<SkillDiscovery>();
        services.TryAddSingleton<LocalSkillCatalog>();
        services.TryAddSingleton<ISkillWorkflowMountPort, NoOpSkillWorkflowMountPort>();
        services.TryAddSingleton<ISkillWorkflowConfirmationPort, NoOpSkillWorkflowConfirmationPort>();
        services.TryAddSingleton<SkillsAgentToolSource>();
        services.TryAddAgentToolSourceAlias<SkillsAgentToolSource>(GetSkillsAgentToolSource);
        return services;
    }

    private static IAgentToolSource GetSkillsAgentToolSource(IServiceProvider sp) =>
        sp.GetRequiredService<SkillsAgentToolSource>();

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
