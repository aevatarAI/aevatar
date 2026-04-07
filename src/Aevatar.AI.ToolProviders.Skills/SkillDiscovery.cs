// ─────────────────────────────────────────────────────────────
// SkillDiscovery — 技能发现
// 扫描目录查找 SKILL.md 文件并解析为 SkillDefinition
// 支持 frontmatter 元数据和旧格式兼容
// ─────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 技能发现器。扫描指定目录下的 SKILL.md 文件。
/// </summary>
public sealed class SkillDiscovery
{
    private readonly SkillFrontmatterParser _parser;
    private readonly ILogger _logger;

    public SkillDiscovery(SkillFrontmatterParser? parser = null, ILogger? logger = null)
    {
        _parser = parser ?? new SkillFrontmatterParser();
        _logger = logger ?? NullLogger.Instance;
    }

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
    /// 支持 frontmatter 格式和旧的 H1 + 首段格式。
    /// </summary>
    private SkillDefinition? ParseSkillFile(string filePath)
    {
        var content = File.ReadAllText(filePath).Trim();
        if (string.IsNullOrEmpty(content)) return null;

        var parsed = _parser.Parse(content);

        // 名称：frontmatter name > 旧格式解析 > 目录名
        var name = parsed.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "unnamed";

        // 描述：frontmatter description > 旧格式解析 > 空
        var description = parsed.Description ?? "";

        // 指令：frontmatter 之后的正文（如果无 frontmatter 则为旧格式解析的 body）
        var instructions = parsed.Body;

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = instructions,
            FilePath = filePath,
            Source = SkillSource.Local,
            Arguments = parsed.Arguments,
            WhenToUse = parsed.WhenToUse,
            IsModelInvocable = parsed.IsModelInvocable,
            IsUserInvocable = parsed.IsUserInvocable,
        };
    }
}
