using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Reads a file from chrono-storage by key.
/// Similar to the Read tool in Claude Code — returns file content with optional line range.
/// </summary>
public sealed class ChronoFileReadTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoFileReadTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_file_read";

    public string Description =>
        "Read a file from chrono-storage by its key path. " +
        "Returns the file content. Supports offset and limit for reading specific line ranges of large files.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key": {
              "type": "string",
              "description": "File path key (e.g. 'workflows/main.yaml', 'connectors.json')"
            },
            "offset": {
              "type": "integer",
              "description": "Start reading from this line number (0-based, default 0)"
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of lines to return (default 2000)"
            }
          },
          "required": ["key"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string key = "";
        int offset = 0;
        int limit = 2000;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("key", out var k))
                key = k.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("offset", out var o) && o.TryGetInt32(out var ov))
                offset = Math.Max(0, ov);
            if (doc.RootElement.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv))
                limit = Math.Clamp(lv, 1, 10000);
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(key))
            return "{\"error\": \"key is required\"}";

        var content = await _client.GetFileAsync(token, key, ct);

        // If it looks like an error response, return as-is
        if (content.TrimStart().StartsWith("{\"error\"", StringComparison.Ordinal))
            return content;

        // Apply offset/limit
        var lines = content.Split('\n');
        var totalLines = lines.Length;
        var sliced = lines.Skip(offset).Take(limit);
        var result = string.Join('\n', sliced);

        return JsonSerializer.Serialize(new
        {
            key,
            totalLines,
            offset,
            limit,
            content = result,
        });
    }
}
