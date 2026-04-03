// ─────────────────────────────────────────────────────────────
// SkillRegistry — 统一技能注册表
// 汇聚本地 + 远程技能，提供查找和系统 prompt 生成
// ─────────────────────────────────────────────────────────────

using System.Text;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 统一技能注册表。管理来自所有来源（本地、远程）的技能。
/// 线程安全，支持运行时动态注册（如远程技能缓存）。
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>注册单个技能。同名覆盖。</summary>
    public void Register(SkillDefinition skill)
    {
        lock (_lock)
            _skills[skill.Name] = skill;
    }

    /// <summary>批量注册技能。</summary>
    public void RegisterRange(IEnumerable<SkillDefinition> skills)
    {
        lock (_lock)
        {
            foreach (var skill in skills)
                _skills[skill.Name] = skill;
        }
    }

    /// <summary>按名称查找技能。</summary>
    public bool TryGet(string nameOrId, out SkillDefinition? skill)
    {
        lock (_lock)
        {
            if (_skills.TryGetValue(nameOrId, out skill))
                return true;

            // 尝试按 RemoteId 匹配
            foreach (var s in _skills.Values)
            {
                if (s.RemoteId != null &&
                    s.RemoteId.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
                {
                    skill = s;
                    return true;
                }
            }

            skill = null;
            return false;
        }
    }

    /// <summary>获取所有已注册技能。</summary>
    public IReadOnlyList<SkillDefinition> GetAll()
    {
        lock (_lock)
            return [.. _skills.Values];
    }

    /// <summary>获取所有允许 LLM 自动调用的技能。</summary>
    public IReadOnlyList<SkillDefinition> GetModelInvocable()
    {
        lock (_lock)
            return _skills.Values.Where(s => s.IsModelInvocable).ToList();
    }

    /// <summary>已注册技能数量。</summary>
    public int Count
    {
        get { lock (_lock) return _skills.Count; }
    }

    /// <summary>
    /// 生成系统 prompt 中的技能列表段落。
    /// 格式：每个技能一行 "- name: description"。
    /// </summary>
    public string BuildSystemPromptSection()
    {
        List<SkillDefinition> skills;
        lock (_lock)
            skills = _skills.Values.Where(s => s.IsModelInvocable).ToList();

        if (skills.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine("You have access to skills — specialized instruction sets for specific tasks.");
        sb.AppendLine("When a user's request matches a skill, invoke it using the `use_skill` tool with the skill name.");
        sb.AppendLine("You can also use `ornn_search_skills` to discover additional skills from the user's Ornn library.");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            var desc = skill.Description;
            // 截断过长描述
            if (desc.Length > 200)
                desc = desc[..197] + "...";

            sb.Append("- **");
            sb.Append(skill.Name);
            sb.Append("**");

            if (!string.IsNullOrEmpty(desc))
            {
                sb.Append(": ");
                sb.Append(desc);
            }

            sb.AppendLine();

            if (!string.IsNullOrEmpty(skill.WhenToUse))
            {
                sb.Append("  When to use: ");
                sb.AppendLine(skill.WhenToUse);
            }
        }

        return sb.ToString();
    }
}
