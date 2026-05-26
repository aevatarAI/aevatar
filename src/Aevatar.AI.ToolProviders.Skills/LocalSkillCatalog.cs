// ─────────────────────────────────────────────────────────────
// LocalSkillCatalog — 本地技能目录
// 仅汇聚本地技能，提供查找和系统 prompt 生成
// ─────────────────────────────────────────────────────────────

using System.Text;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 本地技能目录。管理从本地文件系统发现的技能。
/// 线程安全，仅保存 local skills；远程技能由 use_skill 每次按当前 token 拉取。
/// </summary>
// Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
//   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
//   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
public sealed class LocalSkillCatalog
{
    private readonly Dictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>注册单个本地技能。同名覆盖。</summary>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public void Register(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Source != SkillSource.Local)
            return;

        lock (_lock)
            _skills[skill.Name] = skill;
    }

    /// <summary>批量注册本地技能。</summary>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public void RegisterRange(IEnumerable<SkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        lock (_lock)
        {
            foreach (var skill in skills)
            {
                if (skill.Source == SkillSource.Local)
                    _skills[skill.Name] = skill;
            }
        }
    }

    /// <summary>
    /// 按名称查找本地技能。
    /// </summary>
    /// <param name="name">技能名称。</param>
    /// <param name="skill">命中时的技能定义。</param>
    /// <returns>命中本地技能返回 true。</returns>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public bool TryGet(string name, out SkillDefinition? skill)
    {
        lock (_lock)
        {
            if (_skills.TryGetValue(name, out var localSkill))
            {
                skill = localSkill;
                return true;
            }

            skill = null;
            return false;
        }
    }

    /// <summary>获取所有允许 LLM 自动调用的技能。</summary>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public IReadOnlyList<SkillDefinition> GetModelInvocable()
    {
        lock (_lock)
            return _skills.Values
                .Where(s => s.IsModelInvocable)
                .ToList();
    }

    /// <summary>已注册技能数量。</summary>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public int Count
    {
        get { lock (_lock) return _skills.Count; }
    }

    /// <summary>
    /// 生成系统 prompt 中的技能列表段落。
    /// 格式：每个技能一行 "- name: description"。
    /// </summary>
    // Refactor (iter27/cluster-027-skill-registry-remote-skill-process-state):
    //   Old pattern: SkillRegistry 暴露混合 local + remote skill 注册并用 5min TTL process-wide cache 缓存 remote skill,违反读写分离 + 多用户 token 共享 + 进程内事实状态
    //   New principle: 删 SkillRegistry + TTL tests + 5min cache;新建 local-only LocalSkillCatalog;remote skill 每次 use_skill 调用 IRemoteSkillFetcher.FetchSkillAsync(currentToken, ...) 不缓存;docs/canon factual sync
    public string BuildSystemPromptSection()
    {
        List<SkillDefinition> skills;
        lock (_lock)
            skills = _skills.Values
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
