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
        // Refactor (06-22-ornn-search-org-shared-scope):
        //   Old pattern: a model-facing `scope` knob (public/private/mixed) let the model pick `public`,
        //     which the Ornn server resolves to isPrivate:false only — silently excluding every
        //     org/team-shared skill the caller can actually use.
        //   New principle: a discovery-for-use tool always searches the full accessible set (mixed);
        //     there is no visibility knob to get wrong. Management/ownership flows, if ever needed,
        //     belong in a separate tool, not as a filter on discovery.
        "Search the skills you can use for matching skill packages. This always searches the full set " +
        "you have access to — your own, the public catalog, and skills shared with you directly or through " +
        "your organization/team. There is no visibility filter to choose. " +
        "Call this FIRST whenever the user mentions a named skill (in quotes, slug-like, or Title Case), " +
        "asks which Ornn skills they have, wants to list or browse available skills, " +
        "asks for a specialized capability (translation, content generation, analysis, network or device discovery, " +
        "domain workflows), or says \"挂载/use/load this skill\". " +
        "Also call this when a loaded skill leaves you blocked by a missing capability, unknown workflow step, " +
        "unavailable service, unknown API contract, or repeated tool failure. " +
        "Prefer this over nyxid_proxy path-guessing; proxy discovery lists service APIs, " +
        "this discovers ready-made instruction packages. " +
        "Returns matching skill names + descriptions (the header states how many matched versus shown); " +
        "follow up with use_skill to load and activate one. " +
        "To browse available skills, call this with an empty or omitted query.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search keywords. Omit or pass an empty string to browse available skills." }
          }
        }
        """;

    public bool IsReadOnly => true;

    public string SideEffectKind => "";

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildStructuredResult(
                status: "error",
                query: null,
                scope: null,
                error: "No NyxID access token available. User must be authenticated.",
                matches: Array.Empty<object>(),
                httpStatus: null,
                text: "Error: No NyxID access token available. User must be authenticated.");
        }

        string query = "";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q))
                query = q.GetString() ?? "";
        }
        catch (JsonException) { /* malformed arguments → fall back to an empty (browse-all) query */ }

        // No model-facing scope knob: a discovery-for-use tool must never let the model narrow
        // visibility and hide skills the caller can actually use. Always search the full accessible
        // set (mixed) at the server's max page size so a normal catalog returns in one call. Any
        // `scope` a model puts in the arguments is intentionally ignored.
        const string scope = "mixed";
        var result = await _client.SearchSkillsAsync(token, query, scope, pageSize: 100, ct: ct);

        if (!string.IsNullOrEmpty(result.Error))
        {
            return BuildStructuredResult(
                status: "error",
                query: query,
                scope: scope,
                error: result.Error,
                matches: Array.Empty<object>(),
                httpStatus: null,
                text: $"Search failed: {result.Error}");
        }

        if (result.Items.Count == 0)
        {
            return BuildStructuredResult(
                status: "no_match",
                query: query,
                scope: scope,
                error: null,
                matches: Array.Empty<object>(),
                httpStatus: null,
                text: $"No skills found for query '{query}' (scope: {scope}).");
        }

        var lines = new List<string>
        {
            $"Found {result.Total} skills (showing {result.Items.Count}):",
            "",
        };

        foreach (var skill in result.Items)
        {
            var rawTags = skill.Tags ?? skill.Metadata?.Tags;
            var tags = rawTags != null ? string.Join(", ", rawTags) : "";
            var visibility = skill.IsPrivate ? "private" : "public";
            lines.Add($"- **{skill.Name}** ({visibility}, {skill.Metadata?.Category ?? "unknown"})");
            lines.Add($"  {skill.Description}");
            if (!string.IsNullOrEmpty(tags))
                lines.Add($"  Tags: {tags}");
            lines.Add("");
        }

        lines.Add("Use use_skill with the skill name to load and activate a skill.");
        var matches = result.Items.Select(skill => new
        {
            skill_name = skill.Name ?? string.Empty,
            description = skill.Description,
            is_private = skill.IsPrivate,
            category = skill.Metadata?.Category,
            tags = skill.Tags ?? skill.Metadata?.Tags ?? [],
        }).ToArray();
        return BuildStructuredResult(
            status: "success",
            query: query,
            scope: scope,
            error: null,
            matches: matches,
            httpStatus: null,
            text: string.Join("\n", lines));
    }

    private static string BuildStructuredResult(
        string status,
        string? query,
        string? scope,
        string? error,
        object matches,
        int? httpStatus,
        string text)
    {
        return JsonSerializer.Serialize(new
        {
            result_type = "skill_search",
            status,
            query,
            scope,
            error,
            http_status = httpStatus,
            matches,
            text,
        });
    }
}
