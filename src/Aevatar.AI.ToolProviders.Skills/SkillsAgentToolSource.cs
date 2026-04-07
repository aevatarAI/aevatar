// ─────────────────────────────────────────────────────────────
// SkillsAgentToolSource — 统一技能工具来源
// 扫描本地技能 → 注册到 SkillRegistry → 返回统一 UseSkillTool
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Skills 工具来源。发现本地技能并提供统一的 use_skill 工具。
/// </summary>
public sealed class SkillsAgentToolSource : IAgentToolSource
{
    private readonly SkillsOptions _options;
    private readonly SkillDiscovery _discovery;
    private readonly SkillRegistry _registry;
    private readonly IRemoteSkillFetcher? _remoteFetcher;
    private readonly ILogger _logger;

    public SkillsAgentToolSource(
        SkillsOptions options,
        SkillDiscovery discovery,
        SkillRegistry registry,
        IRemoteSkillFetcher? remoteFetcher = null,
        ILogger<SkillsAgentToolSource>? logger = null)
    {
        _options = options;
        _discovery = discovery;
        _registry = registry;
        _remoteFetcher = remoteFetcher;
        _logger = logger ?? NullLogger<SkillsAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        // 1. 扫描本地目录 → 注册到 SkillRegistry
        foreach (var directory in _options.Directories)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var skills = _discovery.ScanDirectory(directory);
                _registry.RegisterRange(skills);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skill discovery failed for directory {Directory}", directory);
            }
        }

        // 2. 返回统一的 UseSkillTool（单个工具）
        IReadOnlyList<IAgentTool> tools = [new UseSkillTool(_registry, _remoteFetcher)];
        return Task.FromResult(tools);
    }
}
