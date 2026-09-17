using System.Text;
using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.Mainnet.Host.Api.Skills;

// Synthesizes a minimal single `llm_call` workflow for skills that do NOT carry their own workflow YAML.
// The skill's full instructions become the assistant role system prompt; the caller's prompt is the run input.
// Either way (carried or synthesized) the skill runs as one observable workflow run. Mirrors the canonical
// minimal workflow shape (workflows/simple_qa.yaml).
internal static class SkillDirectWorkflowYamlSynthesizer
{
    public static string Synthesize(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var systemPrompt = string.IsNullOrWhiteSpace(skill.Instructions)
            ? skill.Description ?? string.Empty
            : skill.Instructions;

        var builder = new StringBuilder();
        builder.Append("name: ").Append(BuildWorkflowName(skill)).Append('\n');
        builder.Append("roles:\n");
        builder.Append("  - id: assistant\n");
        builder.Append("    name: Assistant\n");
        builder.Append("    system_prompt: |\n");
        AppendBlockScalar(builder, systemPrompt, indent: "      ");
        builder.Append("steps:\n");
        builder.Append("  - id: answer\n");
        builder.Append("    type: llm_call\n");
        builder.Append("    target_role: assistant\n");
        return builder.ToString();
    }

    // Deterministic per-skill workflow name (seeded by the stable ornn id when present).
    private static string BuildWorkflowName(SkillDefinition skill)
    {
        var seed = string.IsNullOrWhiteSpace(skill.RemoteId) ? skill.Name : skill.RemoteId!;
        var slug = new string((seed ?? "skill")
            .Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray()).Trim('-');
        return $"ornn-skill-{(slug.Length == 0 ? "direct" : slug)}";
    }

    // Emits a YAML literal block-scalar body: each source line indented under `system_prompt: |`. Using a block
    // scalar avoids quote/colon/newline escaping issues with arbitrary instruction text.
    private static void AppendBlockScalar(StringBuilder builder, string text, string indent)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length == 0)
                builder.Append('\n');
            else
                builder.Append(indent).Append(line).Append('\n');
        }
    }
}
