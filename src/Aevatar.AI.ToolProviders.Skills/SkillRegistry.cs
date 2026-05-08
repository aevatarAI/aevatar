// ─────────────────────────────────────────────────────────────
// SkillRegistry — 统一技能注册表
// 汇聚本地 + 远程技能，提供查找和系统 prompt 生成
// ─────────────────────────────────────────────────────────────

using System.Text;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 统一技能注册表。管理来自所有来源（本地、远程）的技能。
/// 线程安全，支持运行时动态注册（如远程技能缓存）以及基于 TTL 的失效语义。
/// </summary>
public sealed class SkillRegistry
{
    private readonly Dictionary<string, CachedSkill> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;

    public SkillRegistry()
        : this(TimeProvider.System)
    {
    }

    public SkillRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private sealed record CachedSkill(SkillDefinition Definition, DateTimeOffset FetchedAt);

    /// <summary>注册单个技能。同名覆盖。FetchedAt 戳记为当前时间。</summary>
    public void Register(SkillDefinition skill)
    {
        lock (_lock)
            _skills[skill.Name] = new CachedSkill(skill, _timeProvider.GetUtcNow());
    }

    /// <summary>批量注册技能。共享同一 FetchedAt 时间戳。</summary>
    public void RegisterRange(IEnumerable<SkillDefinition> skills)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var skill in skills)
                _skills[skill.Name] = new CachedSkill(skill, now);
        }
    }

    /// <summary>
    /// 按名称查找技能。
    /// </summary>
    /// <param name="nameOrId">技能名称或 RemoteId。</param>
    /// <param name="skill">命中时的技能定义。</param>
    /// <param name="maxAge">缓存最长有效期。<c>null</c> 表示不检查 TTL（始终算新鲜）。</param>
    /// <returns>命中且未过期返回 true。</returns>
    public bool TryGet(string nameOrId, out SkillDefinition? skill, TimeSpan? maxAge = null)
    {
        lock (_lock)
        {
            if (_skills.TryGetValue(nameOrId, out var cached) && IsFresh(cached, maxAge))
            {
                skill = cached.Definition;
                return true;
            }

            // 尝试按 RemoteId 匹配
            foreach (var entry in _skills.Values)
            {
                if (entry.Definition.RemoteId != null &&
                    entry.Definition.RemoteId.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) &&
                    IsFresh(entry, maxAge))
                {
                    skill = entry.Definition;
                    return true;
                }
            }

            skill = null;
            return false;
        }
    }

    private bool IsFresh(CachedSkill cached, TimeSpan? maxAge)
    {
        if (maxAge is null) return true;
        // TTL only applies to remote skills — local skills are baked in at registration
        // and don't go stale. Without this carve-out, a 5-minute TTL would expire local
        // entries too and `use_skill` would silently lose them after the first cache window.
        if (cached.Definition.Source != SkillSource.Remote) return true;
        return _timeProvider.GetUtcNow() - cached.FetchedAt < maxAge.Value;
    }

    /// <summary>获取所有已注册技能。</summary>
    public IReadOnlyList<SkillDefinition> GetAll()
    {
        lock (_lock)
            return _skills.Values.Select(c => c.Definition).ToArray();
    }

    /// <summary>获取所有允许 LLM 自动调用的技能。</summary>
    public IReadOnlyList<SkillDefinition> GetModelInvocable()
    {
        lock (_lock)
            return _skills.Values
                .Select(c => c.Definition)
                .Where(s => s.IsModelInvocable)
                .ToList();
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
            skills = _skills.Values
                .Select(c => c.Definition)
                .Where(s => s.IsModelInvocable)
                .ToList();

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
