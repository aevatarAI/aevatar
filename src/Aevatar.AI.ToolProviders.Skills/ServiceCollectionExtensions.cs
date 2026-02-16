// ─────────────────────────────────────────────────────────────
// ServiceCollectionExtensions — Skills DI 注册
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>Skills 选项。</summary>
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
        services.TryAddSingleton<SkillDiscovery>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, SkillsAgentToolSource>());
        return services;
    }
}
