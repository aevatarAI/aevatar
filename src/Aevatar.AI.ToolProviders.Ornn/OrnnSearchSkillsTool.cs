using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>搜索用户 Ornn 技能库的工具。</summary>
public sealed class OrnnSearchSkillsTool : IAgentTool
{
    private readonly OrnnSkillClient _client;
    private readonly IRemoteSkillAccessTokenResolver? _remoteAccessTokenResolver;

    public OrnnSearchSkillsTool(
        OrnnSkillClient client,
        IRemoteSkillAccessTokenResolver? remoteAccessTokenResolver = null)
    {
        _client = client;
        _remoteAccessTokenResolver = remoteAccessTokenResolver;
    }

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

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result_type", out var resultType) ||
                resultType.ValueKind != JsonValueKind.String ||
                !string.Equals(resultType.GetString(), "skill_search", StringComparison.Ordinal) ||
                !root.TryGetProperty("status", out var statusValue) ||
                statusValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("matches", out var matches) ||
                matches.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var status = statusValue.GetString();
            if (HasNoError(root) &&
                ((string.Equals(status, "success", StringComparison.Ordinal) && HasNamedMatch(matches)) ||
                 (string.Equals(status, "no_match", StringComparison.Ordinal) && matches.GetArrayLength() == 0)))
            {
                return Receipt(callId, toolName, AgentToolReceiptStatus.Success, resultJson);
            }

            if (string.Equals(status, "error", StringComparison.Ordinal) &&
                matches.GetArrayLength() == 0 &&
                HasNonEmptyString(root, "error"))
            {
                return Receipt(
                    callId,
                    toolName,
                    AgentToolReceiptStatus.Error,
                    resultJson,
                    "ORNN_SKILL_SEARCH_FAILED",
                    "Ornn skill search failed.");
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        string query = "";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var q))
                query = q.GetString() ?? "";
        }
        catch (JsonException) { /* malformed arguments → fall back to an empty (browse-all) query */ }

        string? token;
        if (_remoteAccessTokenResolver is null)
        {
            token = AgentToolRequestContext.NyxIdAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return BuildStructuredResult(
                    status: "error",
                    query: query,
                    scope: null,
                    error: "No NyxID access token available. User must be authenticated.",
                    matches: Array.Empty<object>(),
                    httpStatus: null,
                    text: "Error: No NyxID access token available. User must be authenticated.");
            }
        }
        else
        {
            var resolution = await _remoteAccessTokenResolver.ResolveAsync(query, ct).ConfigureAwait(false);
            if (!resolution.Succeeded)
            {
                var guidance = RemoteSkillAccessTokenFailureMessage.Build(resolution.FailureKind);
                return BuildStructuredResult(
                    status: "error",
                    query: query,
                    scope: null,
                    error: guidance,
                    matches: Array.Empty<object>(),
                    httpStatus: null,
                    text: $"Error: {guidance}");
            }

            token = resolution.AccessToken;
        }

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

    private static bool HasNamedMatch(JsonElement matches)
    {
        foreach (var match in matches.EnumerateArray())
        {
            if (match.ValueKind == JsonValueKind.Object && HasNonEmptyString(match, "skill_name"))
                return true;
        }

        return false;
    }

    private static bool HasNonEmptyString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private static bool HasNoError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
            return true;

        return error.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(error.GetString());
    }

    private AgentToolReceipt Receipt(
        string callId,
        string toolName,
        AgentToolReceiptStatus status,
        string resultJson,
        string errorCode = "",
        string errorMessage = "") =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = status,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            IsDestructive = false,
            ResultJson = resultJson ?? string.Empty,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };

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
