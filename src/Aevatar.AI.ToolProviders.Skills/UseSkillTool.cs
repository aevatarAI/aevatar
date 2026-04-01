// ─────────────────────────────────────────────────────────────
// UseSkillTool — 统一技能调用工具
// LLM 通过此工具调用任何技能（本地或远程）
// 学习 Claude Code SkillTool 模式：单一入口 + 懒加载
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 统一技能调用工具。替代散装的 skill_xxx 工具和 ornn_use_skill 工具。
/// LLM 调用 use_skill(skill="名称") → 返回技能指令内容。
/// </summary>
public sealed class UseSkillTool : IAgentTool
{
    private readonly SkillRegistry _registry;
    private readonly IRemoteSkillFetcher? _remoteFetcher;

    public UseSkillTool(SkillRegistry registry, IRemoteSkillFetcher? remoteFetcher = null)
    {
        _registry = registry;
        _remoteFetcher = remoteFetcher;
    }

    public string Name => "use_skill";

    public string Description =>
        "Load and activate a skill by name. " +
        "Returns the skill's instructions so you can follow them to complete the user's task. " +
        "Proactively use this when a user's request matches a known skill. " +
        "Use ornn_search_skills first to discover skills if you're unsure what's available.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "skill": { "type": "string", "description": "The skill name to invoke" },
            "args": { "type": "string", "description": "Optional arguments for the skill" }
          },
          "required": ["skill"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // ─── 解析参数 ───
        string skillName = "";
        string args = "";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("skill", out var s))
                skillName = s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("args", out var a))
                args = a.GetString() ?? "";
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(skillName))
            return BuildErrorWithAvailableSkills("Error: skill name is required.");

        // ─── 查找技能 ───
        SkillDefinition? skill = null;

        // 1. 从注册表查找（本地 + 已缓存的远程）
        if (_registry.TryGet(skillName, out skill) && skill != null)
            return BuildSkillResponse(skill, args);

        // 2. 尝试从远程拉取
        if (_remoteFetcher != null)
        {
            var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                skill = await _remoteFetcher.FetchSkillAsync(token, skillName, ct);
                if (skill != null)
                {
                    // 缓存到注册表，后续调用不再远程拉取
                    _registry.Register(skill);
                    return BuildSkillResponse(skill, args);
                }
            }
        }

        // 3. 均未找到
        return BuildErrorWithAvailableSkills($"Skill '{skillName}' not found.");
    }

    private static string BuildSkillResponse(SkillDefinition skill, string args)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {skill.Name}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(skill.Description))
        {
            sb.AppendLine(skill.Description);
            sb.AppendLine();
        }

        // 替换参数占位符
        var instructions = skill.Instructions;
        if (!string.IsNullOrEmpty(args))
        {
            instructions = instructions.Replace("$ARGUMENTS", args);

            // 支持位置参数 $0, $1, ...
            var argParts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < argParts.Length && i < 10; i++)
                instructions = instructions.Replace($"${i}", argParts[i]);
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine(instructions);

        // 附带关联文件
        if (skill.AssociatedFiles is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Associated Files");
            sb.AppendLine();
            foreach (var (fileName, content) in skill.AssociatedFiles)
            {
                if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.AppendLine($"### {fileName}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private string BuildErrorWithAvailableSkills(string errorMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(errorMessage);

        var skills = _registry.GetModelInvocable();
        if (skills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available skills:");
            foreach (var s in skills)
            {
                sb.Append("- ");
                sb.Append(s.Name);
                if (!string.IsNullOrEmpty(s.Description))
                {
                    sb.Append(": ");
                    sb.Append(s.Description.Length > 100 ? s.Description[..97] + "..." : s.Description);
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("You can also use ornn_search_skills to discover more skills from the user's library.");

        return sb.ToString();
    }
}
