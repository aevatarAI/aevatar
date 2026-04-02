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
        "Manage external API keys/credentials stored in NyxID. " +
        "Actions: list, rotate (new value), delete.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "rotate", "delete"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "External key ID (for rotate/delete)"
            },
            "credential": {
              "type": "string",
              "description": "New credential value (for rotate)"
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
        var id = args.Str("id");

        return action switch
        {
            "rotate" when !string.IsNullOrWhiteSpace(id) => await RotateAsync(token, id, args, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteExternalKeyAsync(token, id, ct),

            "rotate" or "delete" => $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListExternalKeysAsync(token, ct),
        };
    }

    private async Task<string> RotateAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var cred = args.Str("credential");
        if (string.IsNullOrWhiteSpace(cred))
            return """{"error":"'credential' is required for rotate"}""";
        return await _client.UpdateExternalKeyAsync(token, id,
            JsonSerializer.Serialize(new { credential = cred }), ct);
    }
}
