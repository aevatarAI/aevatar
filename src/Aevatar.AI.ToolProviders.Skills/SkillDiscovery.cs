// ─────────────────────────────────────────────────────────────
// SkillDiscovery — 技能发现
// 扫描目录查找 SKILL.md 文件并解析为 SkillDefinition
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 技能发现器。扫描指定目录下的 SKILL.md 文件。
/// </summary>
public sealed class SkillDiscovery
{
    private readonly ILogger _logger;

    public SkillDiscovery(ILogger? logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// 扫描目录，发现所有 SKILL.md 文件并解析为 SkillDefinition。
    /// </summary>
    public IReadOnlyList<SkillDefinition> ScanDirectory(string directory)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(directory);
        if (expandedPath.StartsWith('~'))
            expandedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expandedPath[2..]);

        if (!Directory.Exists(expandedPath))
        {
            _logger.LogWarning("技能目录不存在: {Dir}", expandedPath);
            return [];
        }

        var skills = new List<SkillDefinition>();
        var files = Directory.GetFiles(expandedPath, "SKILL.md", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                var skill = ParseSkillFile(file);
                if (skill != null)
                {
                    skills.Add(skill);
                    _logger.LogInformation("发现技能: {Name} ({Path})", skill.Name, file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析技能文件失败: {Path}", file);
            }
        }

        _logger.LogInformation("技能扫描完成: {Dir} → {Count} 个技能", expandedPath, skills.Count);
        return skills;
    }

    /// <summary>
    /// 解析 SKILL.md 文件为 SkillDefinition。
    /// SKILL.md 格式：第一行为 # 标题（技能名），第二段为描述，其余为指令。
    /// </summary>
    private static SkillDefinition? ParseSkillFile(string filePath)
    {
        var content = File.ReadAllText(filePath).Trim();
        if (string.IsNullOrEmpty(content)) return null;

        var lines = content.Split('\n');
        var name = lines[0].TrimStart('#', ' ').Trim();
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "unnamed";

        // 找到第一个非空段落作为描述
        var description = "";
        var instructionsStart = 1;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(description))
            {
                instructionsStart = i + 1;
                break;
            }
            if (!string.IsNullOrEmpty(line))
                description += (description.Length > 0 ? " " : "") + line;
        }

        var instructions = string.Join("\n", lines.Skip(instructionsStart));

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            FilePath = filePath,
        };
    }
}
