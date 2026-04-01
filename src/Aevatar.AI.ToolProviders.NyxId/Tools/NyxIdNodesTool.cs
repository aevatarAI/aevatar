using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID on-premise node agents.</summary>
public sealed class NyxIdNodesTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdNodesTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_nodes";

    public string Description =>
        "Manage on-premise node agents. " +
        "Actions: list, show, delete, register_token, rotate_token.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "delete", "register_token", "rotate_token"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Node ID (for show/delete/rotate_token)"
            },
            "name": {
              "type": "string",
              "description": "Node name (for register_token)"
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
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetNodeAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteNodeAsync(token, id, ct),
            "rotate_token" when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateNodeTokenAsync(token, id, ct),
            "register_token" => await RegisterTokenAsync(token, args, ct),

            "show" or "delete" or "rotate_token" =>
                $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListNodesAsync(token, ct),
        };
    }

    private async Task<string> RegisterTokenAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'name' is required for register_token"}""";
        return await _client.GenerateNodeRegistrationTokenAsync(token,
            JsonSerializer.Serialize(new { name }), ct);
    }
}
