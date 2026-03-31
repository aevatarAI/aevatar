using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>搜索用户 Ornn 技能库的工具。</summary>
public sealed class OrnnSearchSkillsTool : IAgentTool
{
    private readonly OrnnSkillClient _client;

    public OrnnSearchSkillsTool(OrnnSkillClient client) => _client = client;

    public string Name => "ornn_search_skills";

    public string Description =>
        "Search for skills in your Ornn skill library. " +
        "Returns skill names, descriptions, and IDs that can be loaded with ornn_use_skill.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search keywords" },
            "scope": { "type": "string", "enum": ["public", "private", "mixed"], "description": "Search scope (default: mixed)" }
          },
          "required": ["query"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string query = "";
        string scope = "mixed";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q))
                query = q.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("scope", out var s))
                scope = s.GetString() ?? "mixed";
        }
        catch { /* use defaults */ }

        var result = await _client.SearchSkillsAsync(token, query, scope, ct: ct);

        if (!string.IsNullOrEmpty(result.Error))
            return $"Search failed: {result.Error}";

        if (result.Items.Count == 0)
            return $"No skills found for query '{query}' (scope: {scope}).";

        var lines = new List<string>
        {
            $"Found {result.Total} skills (showing {result.Items.Count}):",
            "",
        };

        foreach (var skill in result.Items)
        {
            var tags = skill.Metadata?.Tags != null ? string.Join(", ", skill.Metadata.Tags) : "";
            var visibility = skill.IsPrivate ? "private" : "public";
            lines.Add($"- **{skill.Name}** ({visibility}, {skill.Metadata?.Category ?? "unknown"})");
            lines.Add($"  {skill.Description}");
            if (!string.IsNullOrEmpty(tags))
                lines.Add($"  Tags: {tags}");
            lines.Add("");
        }

        lines.Add("Use ornn_use_skill with the skill name to load and use a skill.");
        return string.Join("\n", lines);
    }
}
