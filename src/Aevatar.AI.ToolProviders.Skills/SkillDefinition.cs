// ─────────────────────────────────────────────────────────────
// SkillDefinition — 技能定义（从 SKILL.md 解析）
// ─────────────────────────────────────────────────────────────

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 技能定义。从 SKILL.md 文件解析得到。
/// 包含名称、描述、参数说明和指令内容。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>技能名称。</summary>
    public required string Name { get; init; }

    /// <summary>技能描述（用于 LLM 理解技能用途）。</summary>
    public required string Description { get; init; }

    /// <summary>技能指令内容（SKILL.md 正文）。</summary>
    public required string Instructions { get; init; }

    /// <summary>技能文件路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>技能目录路径。</summary>
    public string DirectoryPath => Path.GetDirectoryName(FilePath) ?? "";
}
