using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID API keys for programmatic access.</summary>
public sealed class NyxIdApiKeysTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdApiKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_api_keys";

    public string Description =>
        "Manage NyxID API keys for programmatic access. " +
        "Use 'list' to see existing keys, or 'create' to generate a new key with specified scopes.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "create"],
              "description": "Action: 'list' existing keys or 'create' a new key"
            },
            "name": {
              "type": "string",
              "description": "Name for the new key (required for 'create')"
            },
            "scopes": {
              "type": "string",
              "description": "Space-separated scopes for the new key (e.g. 'proxy read')"
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string action = "list";
        string? name = null;
        string? scopes = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("name", out var n))
                name = n.GetString();
            if (doc.RootElement.TryGetProperty("scopes", out var s))
                scopes = s.GetString();
        }
        catch { /* use defaults */ }

        if (action == "create")
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Error: 'name' is required for create action.";

            var body = JsonSerializer.Serialize(new { name, scopes = scopes ?? "proxy read" });
            return await _client.CreateApiKeyAsync(token, body, ct);
        }

        return await _client.ListApiKeysAsync(token, ct);
    }
}
