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
        "Actions: update (name), delete, consents (list), revoke_consent.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["update", "delete", "consents", "revoke_consent"],
              "description": "Action to perform (default: consents)"
            },
            "name": {
              "type": "string",
              "description": "New display name (for update)"
            },
            "client_id": {
              "type": "string",
              "description": "OAuth client ID (for revoke_consent)"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var action = args.Str("action", "consents");

        return action switch
        {
            "update" => await UpdateAsync(token, args, ct),
            "delete" => await _client.DeleteAccountAsync(token, ct),
            "revoke_consent" => await RevokeConsentAsync(token, args, ct),
            _ => await _client.ListConsentsAsync(token, ct),
        };
    }

    private async Task<string> UpdateAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("name") ?? args.Str("display_name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'name' is required for update"}""";
        return await _client.UpdateProfileAsync(token,
            JsonSerializer.Serialize(new { display_name = name }), ct);
    }

    private async Task<string> RevokeConsentAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var clientId = args.Str("client_id");
        if (string.IsNullOrWhiteSpace(clientId))
            return """{"error":"'client_id' is required for revoke_consent"}""";
        return await _client.RevokeConsentAsync(token, clientId, ct);
    }
}
