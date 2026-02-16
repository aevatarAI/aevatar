// ─────────────────────────────────────────────────────────────
// SkillToolAdapter — 将 Skill 适配为 IAgentTool
// 执行时返回技能的指令内容给 LLM
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// 将 SkillDefinition 适配为 IAgentTool。
/// LLM 调用此工具时，返回技能的指令内容。
/// </summary>
public sealed class SkillToolAdapter : IAgentTool
{
    private readonly SkillDefinition _skill;

    public SkillToolAdapter(SkillDefinition skill) => _skill = skill;

    /// <summary>工具名称（skill_ 前缀 + 技能名）。</summary>
    public string Name => $"skill_{_skill.Name.ToLowerInvariant().Replace(' ', '_')}";

    /// <summary>技能描述。</summary>
    public string Description => _skill.Description;

    /// <summary>参数 schema（技能工具接受一个 query 参数）。</summary>
    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "用户的具体问题或指令" }
          },
          "required": ["query"]
        }
        """;

    /// <summary>执行：返回技能指令内容。</summary>
    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // 技能工具的"执行"就是把指令内容返回给 LLM
        // LLM 会根据指令内容来完成具体任务
        var result = $"# {_skill.Name}\n\n{_skill.Instructions}";
        return Task.FromResult(result);
    }
}
