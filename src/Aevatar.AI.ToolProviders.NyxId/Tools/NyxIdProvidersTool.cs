using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage OAuth provider connections in NyxID.</summary>
public sealed class NyxIdProvidersTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdProvidersTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_providers";

    public string Description =>
        "Manage OAuth provider connections. " +
        "Actions: list, connect_oauth, connect_device_code, poll_device_code, disconnect, " +
        "set_credentials, get_credentials, delete_credentials.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "connect_oauth", "connect_device_code", "poll_device_code", "disconnect", "set_credentials", "get_credentials", "delete_credentials"],
              "description": "Action to perform (default: list)"
            },
            "provider_id": {
              "type": "string",
              "description": "Provider config ID (from catalog entry's provider_config_id)"
            },
            "state": {
              "type": "string",
              "description": "State token from connect_device_code (for poll_device_code)"
            },
            "client_id": {
              "type": "string",
              "description": "OAuth app client ID (for set_credentials)"
            },
            "client_secret": {
              "type": "string",
              "description": "OAuth app client secret (for set_credentials)"
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
        var action = args.Str("action", "list");
        var providerId = args.Str("provider_id");

        return action switch
        {
            "connect_oauth" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.InitiateOAuthConnectAsync(token, providerId, ct),
            "connect_device_code" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.InitiateDeviceCodeAsync(token, providerId, ct),
            "poll_device_code" when !string.IsNullOrWhiteSpace(providerId) =>
                await PollDeviceCodeAsync(token, providerId, args, ct),
            "disconnect" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.DisconnectProviderAsync(token, providerId, ct),
            "set_credentials" when !string.IsNullOrWhiteSpace(providerId) =>
                await SetCredentialsAsync(token, providerId, args, ct),
            "get_credentials" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.GetUserCredentialsAsync(token, providerId, ct),
            "delete_credentials" when !string.IsNullOrWhiteSpace(providerId) =>
                await _client.DeleteUserCredentialsAsync(token, providerId, ct),

            "list" => await _client.ListProviderTokensAsync(token, ct),

            _ when string.IsNullOrWhiteSpace(providerId) =>
                $"{{\"error\":\"'provider_id' is required for {action}\"}}",
            _ => await _client.ListProviderTokensAsync(token, ct),
        };
    }

    private async Task<string> PollDeviceCodeAsync(string token, string providerId, ToolArgs args, CancellationToken ct)
    {
        var state = args.Str("state");
        if (string.IsNullOrWhiteSpace(state))
            return """{"error":"'state' is required for poll_device_code"}""";
        return await _client.PollDeviceCodeAsync(token, providerId, state, ct);
    }

    private async Task<string> SetCredentialsAsync(string token, string providerId, ToolArgs args, CancellationToken ct)
    {
        var clientId = args.Str("client_id");
        if (string.IsNullOrWhiteSpace(clientId))
            return """{"error":"'client_id' is required for set_credentials"}""";
        return await _client.SetUserCredentialsAsync(token, providerId,
            JsonSerializer.Serialize(new { client_id = clientId, client_secret = args.Str("client_secret") }), ct);
    }
}
