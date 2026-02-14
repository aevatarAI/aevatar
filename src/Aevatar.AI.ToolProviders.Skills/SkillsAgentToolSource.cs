using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Skills 工具来源。扫描技能目录并将技能适配为 IAgentTool。
/// </summary>
public sealed class SkillsAgentToolSource : IAgentToolSource
{
    private readonly SkillsOptions _options;
    private readonly SkillDiscovery _discovery;
    private readonly ILogger _logger;

    public SkillsAgentToolSource(
        SkillsOptions options,
        SkillDiscovery discovery,
        ILogger<SkillsAgentToolSource>? logger = null)
    {
        _options = options;
        _discovery = discovery;
        _logger = logger ?? NullLogger<SkillsAgentToolSource>.Instance;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in _options.Directories)
        {
            if (ct.IsCancellationRequested) break;

            IReadOnlyList<SkillDefinition> skills;
            try
            {
                skills = _discovery.ScanDirectory(directory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skill discovery failed for directory {Directory}", directory);
                continue;
            }

            foreach (var skill in skills)
            {
                var adapter = new SkillToolAdapter(skill);
                tools[adapter.Name] = adapter;
            }
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>([..tools.Values]);
    }
}
