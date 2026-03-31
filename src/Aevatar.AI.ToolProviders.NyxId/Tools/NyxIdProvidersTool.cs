using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Tool to manage OAuth provider connections in NyxID.
/// Enables initiating OAuth flows directly from chat by returning authorization URLs,
/// and managing user-provided OAuth app credentials for providers that require them.
/// </summary>
public sealed class NyxIdProvidersTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdProvidersTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_providers";

    public string Description =>
        "Manage OAuth provider connections in NyxID. " +
        "Actions: 'list' connected providers, 'connect_oauth' to initiate OAuth (returns authorization URL), " +
        "'connect_device_code' to start device code flow, 'poll_device_code' to check status, " +
        "'disconnect' to remove a connection, " +
        "'set_credentials' to store user's own OAuth app credentials (client_id + client_secret), " +
        "'get_credentials' to check if credentials are configured, " +
        "'delete_credentials' to remove stored credentials.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "connect_oauth", "connect_device_code", "poll_device_code", "disconnect", "set_credentials", "get_credentials", "delete_credentials"],
              "description": "Action to perform"
            },
            "provider_id": {
              "type": "string",
              "description": "Provider config ID (required for all actions except 'list'). Get this from the catalog entry's provider_config_id field."
            },
            "state": {
              "type": "string",
              "description": "State token returned by connect_device_code (required for poll_device_code)"
            },
            "client_id": {
              "type": "string",
              "description": "OAuth app client ID (required for set_credentials)"
            },
            "client_secret": {
              "type": "string",
              "description": "OAuth app client secret (required for set_credentials)"
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
        string? providerId = null;
        string? state = null;
        string? clientId = null;
        string? clientSecret = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("provider_id", out var p))
                providerId = p.GetString();
            if (doc.RootElement.TryGetProperty("state", out var s))
                state = s.GetString();
            if (doc.RootElement.TryGetProperty("client_id", out var cid))
                clientId = cid.GetString();
            if (doc.RootElement.TryGetProperty("client_secret", out var csec))
                clientSecret = csec.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "connect_oauth" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.InitiateOAuthConnectAsync(token, providerId, ct),
            "connect_device_code" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.InitiateDeviceCodeAsync(token, providerId, ct),
            "poll_device_code" when !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(state) =>
                await _client.PollDeviceCodeAsync(token, providerId, state, ct),
            "disconnect" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.DisconnectProviderAsync(token, providerId, ct),

            "set_credentials" when !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(clientId) =>
                await _client.SetUserCredentialsAsync(token, providerId,
                    JsonSerializer.Serialize(new { client_id = clientId, client_secret = clientSecret }), ct),
            "get_credentials" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.GetUserCredentialsAsync(token, providerId, ct),
            "delete_credentials" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.DeleteUserCredentialsAsync(token, providerId, ct),

            "connect_oauth" or "connect_device_code" or "disconnect"
                or "get_credentials" or "delete_credentials" =>
                "Error: 'provider_id' is required for this action.",
            "set_credentials" =>
                "Error: 'provider_id' and 'client_id' are required for set_credentials.",
            "poll_device_code" =>
                "Error: 'provider_id' and 'state' are required for poll_device_code.",

            _ => await _client.ListProviderTokensAsync(token, ct),
        };
    }
}
