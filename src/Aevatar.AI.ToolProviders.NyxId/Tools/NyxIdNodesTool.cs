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
        "Manage on-premise node agents. Nodes hold credentials locally and proxy requests through NyxID. " +
        "Actions: 'list' all nodes, 'show' details, 'delete' a node, " +
        "'register_token' to generate a registration token for a new node, " +
        "'rotate_token' to rotate a node's auth token.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "delete", "register_token", "rotate_token"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "Node ID (required for 'show', 'delete', 'rotate_token')"
            },
            "name": {
              "type": "string",
              "description": "Node name (required for 'register_token')"
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
        string? name = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("name", out var n))
                name = n.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetNodeAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteNodeAsync(token, id, ct),
            "rotate_token" when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateNodeTokenAsync(token, id, ct),
            "register_token" when !string.IsNullOrWhiteSpace(name) =>
                await _client.GenerateNodeRegistrationTokenAsync(token,
                    JsonSerializer.Serialize(new { name }), ct),

            "show" or "delete" or "rotate_token" =>
                $"Error: 'id' is required for {action} action.",
            "register_token" => "Error: 'name' is required for register_token action.",
            _ => await _client.ListNodesAsync(token, ct),
        };
    }
}
