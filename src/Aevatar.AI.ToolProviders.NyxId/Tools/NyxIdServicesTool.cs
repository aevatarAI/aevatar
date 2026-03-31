using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage user's connected services in NyxID.</summary>
public sealed class NyxIdServicesTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdServicesTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_services";

    public string Description =>
        "Manage the user's connected services in NyxID. " +
        "Use 'list' to see all connected services, 'show' for details on a specific service, " +
        "or 'delete' to remove a service connection.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "delete"],
              "description": "Action: 'list' all services, 'show' service details, or 'delete' a service"
            },
            "id": {
              "type": "string",
              "description": "Service ID (required for 'show' and 'delete')"
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
                await _client.GetServiceAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteServiceAsync(token, id, ct),
            "delete" => "Error: 'id' is required for delete action.",
            "show" => "Error: 'id' is required for show action.",
            _ => await _client.ListServicesAsync(token, ct),
        };
    }
}
