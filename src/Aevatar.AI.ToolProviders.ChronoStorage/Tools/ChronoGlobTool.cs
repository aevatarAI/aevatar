using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Lists files in chrono-storage matching a glob pattern.
/// Similar to the Glob tool in Claude Code — finds files by name/path patterns.
/// </summary>
public sealed class ChronoGlobTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoGlobTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_glob";

    public string Description =>
        "List files in chrono-storage matching a glob pattern. " +
        "Use this to find files by name or path pattern (e.g. 'workflows/*.yaml', '**/*.json'). " +
        "Returns file keys, types, and timestamps.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "pattern": {
              "type": "string",
              "description": "Glob pattern to match file paths (e.g. 'workflows/*.yaml', '**/*.json', 'config.json'). Supports * and ** wildcards."
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

        string pattern = "*";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("pattern", out var p))
                pattern = p.GetString() ?? "*";
        }
        catch { /* use default */ }

        var manifestJson = await _client.GetManifestAsync(token, ct);

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            if (!manifest.RootElement.TryGetProperty("files", out var files))
                return "[]";

            var regex = GlobToRegex(pattern);
            var matches = new List<JsonElement>();
            foreach (var file in files.EnumerateArray())
            {
                var key = file.GetProperty("key").GetString() ?? "";
                if (regex.IsMatch(key))
                    matches.Add(file);
            }

            return JsonSerializer.Serialize(matches);
        }
        catch
        {
            return manifestJson; // pass through any error
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        var regexPattern = "^" + Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
