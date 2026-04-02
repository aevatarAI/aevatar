using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Searches file contents in chrono-storage using a regex or literal pattern.
/// Similar to the Grep tool in Claude Code — finds content matches across files.
/// </summary>
public sealed class ChronoGrepTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoGrepTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_grep";

    public string Description =>
        "Search file contents in chrono-storage using a regex or literal pattern. " +
        "Returns matching lines with file key, line number, and snippet. " +
        "Optionally filter by file glob pattern.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Regex or literal text pattern to search for in file contents"
            },
            "glob": {
              "type": "string",
              "description": "Optional glob pattern to filter which files to search (e.g. '*.json', 'workflows/*.yaml')"
            },
            "maxResults": {
              "type": "integer",
              "description": "Maximum number of matching lines to return (default 50, max 100)"
            }
          },
          "required": ["pattern"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string pattern = "";
        string? glob = null;
        int? maxResults = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("pattern", out var p))
                pattern = p.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("glob", out var g))
                glob = g.GetString();
            if (doc.RootElement.TryGetProperty("maxResults", out var m) && m.TryGetInt32(out var mr))
                maxResults = mr;
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(pattern))
            return "{\"error\": \"pattern is required\"}";

        return await _client.GrepAsync(token, pattern, glob, maxResults, ct);
    }
}
