using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Performs exact string replacement in a chrono-storage file.
/// Similar to the Edit tool in Claude Code — replaces old_string with new_string.
/// </summary>
public sealed class ChronoFileEditTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoFileEditTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_file_edit";

    public string Description =>
        "Edit a file in chrono-storage by performing exact string replacement. " +
        "Reads the file, replaces old_string with new_string, and writes back. " +
        "The old_string must match exactly once in the file (unless replace_all is true).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key": {
              "type": "string",
              "description": "File path key (e.g. 'connectors.json', 'workflows/main.yaml')"
            },
            "old_string": {
              "type": "string",
              "description": "The exact text to find and replace"
            },
            "new_string": {
              "type": "string",
              "description": "The replacement text"
            },
            "replace_all": {
              "type": "boolean",
              "description": "Replace all occurrences instead of requiring exactly one match (default false)"
            }
          },
          "required": ["key", "old_string", "new_string"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string key = "";
        string oldString = "";
        string newString = "";
        bool replaceAll = false;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("key", out var k))
                key = k.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("old_string", out var o))
                oldString = o.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("new_string", out var n))
                newString = n.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("replace_all", out var r))
                replaceAll = r.GetBoolean();
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(key))
            return "{\"error\": \"key is required\"}";
        if (string.IsNullOrEmpty(oldString))
            return "{\"error\": \"old_string is required\"}";

        // Read current content
        var content = await _client.GetFileAsync(token, key, ct);
        if (content.TrimStart().StartsWith("{\"error\"", StringComparison.Ordinal))
            return content;

        // Count occurrences
        var count = CountOccurrences(content, oldString);
        if (count == 0)
            return JsonSerializer.Serialize(new { error = $"old_string not found in file '{key}'" });

        if (!replaceAll && count > 1)
            return JsonSerializer.Serialize(new { error = $"old_string found {count} times in file '{key}'. Use replace_all=true or provide more context to make it unique." });

        // Perform replacement
        var newContent = replaceAll
            ? content.Replace(oldString, newString)
            : ReplaceFirst(content, oldString, newString);

        var result = await _client.PutFileAsync(token, key, newContent, ct);

        if (string.IsNullOrWhiteSpace(result))
            return JsonSerializer.Serialize(new { key, success = true, replacements = replaceAll ? count : 1 });

        return result;
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
