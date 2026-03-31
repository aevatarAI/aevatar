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
        "Use 'list' to see all nodes, 'show' for details, or 'delete' to remove a node.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "delete"],
              "description": "Action: 'list' all nodes, 'show' node details, or 'delete' a node"
            },
            "id": {
              "type": "string",
              "description": "Node ID or name (required for 'show' and 'delete')"
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

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetNodeAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteNodeAsync(token, id, ct),
            "show" or "delete" => $"Error: 'id' is required for {action} action.",
            _ => await _client.ListNodesAsync(token, ct),
        };
    }
}
