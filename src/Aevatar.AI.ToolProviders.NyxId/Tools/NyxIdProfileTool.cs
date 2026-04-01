using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID user profile and OAuth consents.</summary>
public sealed class NyxIdProfileTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdProfileTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_profile";

    public string Description =>
        "Manage the user's NyxID profile. " +
        "Actions: 'update' profile (display name), 'delete' account, " +
        "'consents' to list OAuth app consents, 'revoke_consent' to revoke an app's consent.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["update", "delete", "consents", "revoke_consent"],
              "description": "Action to perform"
            },
            "name": {
              "type": "string",
              "description": "New display name (for 'update')"
            },
            "client_id": {
              "type": "string",
              "description": "OAuth client ID to revoke consent for (for 'revoke_consent')"
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

        string action = "consents";
        string? name = null;
        string? clientId = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "consents";
            if (doc.RootElement.TryGetProperty("name", out var n))
                name = n.GetString();
            if (doc.RootElement.TryGetProperty("client_id", out var c))
                clientId = c.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "update" when !string.IsNullOrWhiteSpace(name) =>
                await _client.UpdateProfileAsync(token,
                    JsonSerializer.Serialize(new { display_name = name }), ct),
            "update" => "Error: 'name' is required for update action.",
            "delete" => await _client.DeleteAccountAsync(token, ct),
            "revoke_consent" when !string.IsNullOrWhiteSpace(clientId) =>
                await _client.RevokeConsentAsync(token, clientId, ct),
            "revoke_consent" => "Error: 'client_id' is required for revoke_consent action.",
            _ => await _client.ListConsentsAsync(token, ct),
        };
    }
}
