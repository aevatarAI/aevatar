using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>加载并使用指定 Ornn 技能的工具。</summary>
public sealed class OrnnUseSkillTool : IAgentTool
{
    private readonly OrnnSkillClient _client;

    public OrnnUseSkillTool(OrnnSkillClient client) => _client = client;

    public string Name => "ornn_use_skill";

    public string Description =>
        "Load a specific Ornn skill by name or ID. " +
        "Returns the skill's instructions and associated files so you can follow them.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "skill_name_or_id": { "type": "string", "description": "Skill name or GUID" }
          },
          "required": ["skill_name_or_id"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string skillId = "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("skill_name_or_id", out var v))
                skillId = v.GetString() ?? "";
        }
        catch { /* use empty */ }

        if (string.IsNullOrWhiteSpace(skillId))
            return "Error: skill_name_or_id is required.";

        var skill = await _client.GetSkillJsonAsync(token, skillId, ct);
        if (skill == null)
            return $"Skill '{skillId}' not found or access denied.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {skill.Name}");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(skill.Description))
        {
            sb.AppendLine(skill.Description);
            sb.AppendLine();
        }

        if (skill.Files != null && skill.Files.Count > 0)
        {
            // SKILL.md content first
            if (skill.Files.TryGetValue("SKILL.md", out var skillMd))
            {
                sb.AppendLine("## Instructions");
                sb.AppendLine();
                sb.AppendLine(skillMd);
                sb.AppendLine();
            }

            // Other files
            var otherFiles = skill.Files.Where(f => f.Key != "SKILL.md").ToList();
            if (otherFiles.Count > 0)
            {
                sb.AppendLine("## Associated Files");
                sb.AppendLine();
                foreach (var (fileName, content) in otherFiles)
                {
                    sb.AppendLine($"### {fileName}");
                    sb.AppendLine("```");
                    sb.AppendLine(content);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }
}
