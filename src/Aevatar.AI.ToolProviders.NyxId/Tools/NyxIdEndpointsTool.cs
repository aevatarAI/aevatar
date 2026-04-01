using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID user endpoints (service base URLs).</summary>
public sealed class NyxIdEndpointsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdEndpointsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_endpoints";

    public string Description =>
        "Manage user endpoints (service base URLs). " +
        "Actions: 'list' all endpoints, 'update' an endpoint's URL, 'delete' an endpoint.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "update", "delete"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "Endpoint ID (required for 'update' and 'delete')"
            },
            "url": {
              "type": "string",
              "description": "New URL (required for 'update')"
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
        string? url = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("url", out var u))
                url = u.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "update" when !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(url) =>
                await _client.UpdateEndpointAsync(token, id,
                    JsonSerializer.Serialize(new { url }), ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteEndpointAsync(token, id, ct),

            "update" when string.IsNullOrWhiteSpace(id) => "Error: 'id' is required for update action.",
            "update" => "Error: 'url' is required for update action.",
            "delete" => "Error: 'id' is required for delete action.",
            _ => await _client.ListEndpointsAsync(token, ct),
        };
    }
}
