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
        "Actions: list, update, delete.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "update", "delete"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Endpoint ID (for update/delete)"
            },
            "url": {
              "type": "string",
              "description": "New URL (for update)"
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
            "update" when !string.IsNullOrWhiteSpace(id) => await UpdateAsync(token, id, args, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteEndpointAsync(token, id, ct),

            "update" or "delete" => $"{{\"error\":\"'id' is required for {action}\"}}",
            _ => await _client.ListEndpointsAsync(token, ct),
        };
    }

    private async Task<string> UpdateAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var url = args.Str("url");
        if (string.IsNullOrWhiteSpace(url))
            return """{"error":"'url' is required for update"}""";
        return await _client.UpdateEndpointAsync(token, id, JsonSerializer.Serialize(new { url }), ct);
    }
}
