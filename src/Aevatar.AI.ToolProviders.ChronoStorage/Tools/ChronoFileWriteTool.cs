using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Writes/creates a file in chrono-storage.
/// Similar to the Write tool in Claude Code — creates or overwrites a file with given content.
/// </summary>
public sealed class ChronoFileWriteTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoFileWriteTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_file_write";

    public string Description =>
        "Write or create a file in chrono-storage. " +
        "This will overwrite the file if it already exists, or create it if it doesn't. " +
        "Use chrono_file_edit for precise string replacements instead of full rewrites.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key": {
              "type": "string",
              "description": "File path key (e.g. 'workflows/new-flow.yaml', 'scripts/helper.cs')"
            },
            "content": {
              "type": "string",
              "description": "The full content to write to the file"
            }
          },
          "required": ["key", "content"]
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string key = "";
        string content = "";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("key", out var k))
                key = k.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("content", out var c))
                content = c.GetString() ?? "";
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(key))
            return "{\"error\": \"key is required\"}";

        var result = await _client.PutFileAsync(token, key, content, ct);

        // PutFile returns empty on success (204 No Content)
        if (string.IsNullOrWhiteSpace(result))
            return JsonSerializer.Serialize(new { key, success = true });

        return result;
    }
}
