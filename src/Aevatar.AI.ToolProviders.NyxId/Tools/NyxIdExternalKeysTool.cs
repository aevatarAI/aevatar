using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID external API keys/credentials.</summary>
public sealed class NyxIdExternalKeysTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdExternalKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_external_keys";

    public string Description =>
        "Manage external API keys and credentials stored in NyxID. " +
        "Actions: 'list' all external credentials, " +
        "'rotate' to update a credential with a new value, " +
        "'delete' to remove a credential.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "rotate", "delete"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "External key ID (required for 'rotate' and 'delete')"
            },
            "credential": {
              "type": "string",
              "description": "New credential value (required for 'rotate')"
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
        string? id = null;
        string? credential = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("credential", out var c))
                credential = c.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "rotate" when !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(credential) =>
                await _client.UpdateExternalKeyAsync(token, id,
                    JsonSerializer.Serialize(new { credential }), ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteExternalKeyAsync(token, id, ct),

            "rotate" when string.IsNullOrWhiteSpace(id) => "Error: 'id' is required for rotate action.",
            "rotate" => "Error: 'credential' is required for rotate action.",
            "delete" => "Error: 'id' is required for delete action.",
            _ => await _client.ListExternalKeysAsync(token, ct),
        };
    }
}
