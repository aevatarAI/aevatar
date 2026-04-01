using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID API keys for programmatic access.</summary>
public sealed class NyxIdApiKeysTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdApiKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_api_keys";

    public string Description =>
        "Manage NyxID API keys for programmatic access. " +
        "Actions: 'list' all keys, 'show' key details, 'create' a new key, " +
        "'rotate' to regenerate a key, 'delete' to revoke a key, " +
        "'update' to change key name/scopes/permissions.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "rotate", "delete", "update"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "API key ID (required for 'show', 'rotate', 'delete', 'update')"
            },
            "name": {
              "type": "string",
              "description": "Key name (required for 'create', optional for 'update')"
            },
            "scopes": {
              "type": "string",
              "description": "Space-separated scopes (e.g. 'proxy read write')"
            },
            "allowed_services": {
              "type": "string",
              "description": "Comma-separated service IDs to allow (for 'create' or 'update')"
            },
            "allowed_nodes": {
              "type": "string",
              "description": "Comma-separated node IDs to allow (for 'create' or 'update')"
            },
            "allow_all_services": {
              "type": "boolean",
              "description": "Allow access to all services (for 'create' or 'update')"
            },
            "allow_all_nodes": {
              "type": "boolean",
              "description": "Allow access to all nodes (for 'create' or 'update')"
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
        string? scopes = null;
        string? allowedServices = null;
        string? allowedNodes = null;
        bool? allowAllServices = null;
        bool? allowAllNodes = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("name", out var n))
                name = n.GetString();
            if (doc.RootElement.TryGetProperty("scopes", out var s))
                scopes = s.GetString();
            if (doc.RootElement.TryGetProperty("allowed_services", out var asv))
                allowedServices = asv.GetString();
            if (doc.RootElement.TryGetProperty("allowed_nodes", out var anv))
                allowedNodes = anv.GetString();
            if (doc.RootElement.TryGetProperty("allow_all_services", out var aas))
                allowAllServices = aas.GetBoolean();
            if (doc.RootElement.TryGetProperty("allow_all_nodes", out var aan))
                allowAllNodes = aan.GetBoolean();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetApiKeyAsync(token, id, ct),
            "rotate" when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateApiKeyAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteApiKeyAsync(token, id, ct),
            "update" when !string.IsNullOrWhiteSpace(id) =>
                await UpdateApiKeyAsync(token, id, name, scopes, allowedServices, allowedNodes, allowAllServices, allowAllNodes, ct),
            "create" when !string.IsNullOrWhiteSpace(name) =>
                await CreateApiKeyAsync(token, name, scopes, allowedServices, allowedNodes, allowAllServices, allowAllNodes, ct),

            "show" or "rotate" or "delete" or "update" =>
                "Error: 'id' is required for this action.",
            "create" => "Error: 'name' is required for create action.",
            _ => await _client.ListApiKeysAsync(token, ct),
        };
    }

    private async Task<string> CreateApiKeyAsync(
        string token, string name, string? scopes,
        string? allowedServices, string? allowedNodes,
        bool? allowAllServices, bool? allowAllNodes,
        CancellationToken ct)
    {
        var payload = BuildKeyPayload(name, scopes, allowedServices, allowedNodes, allowAllServices, allowAllNodes);
        return await _client.CreateApiKeyAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateApiKeyAsync(
        string token, string id, string? name, string? scopes,
        string? allowedServices, string? allowedNodes,
        bool? allowAllServices, bool? allowAllNodes,
        CancellationToken ct)
    {
        var payload = BuildKeyPayload(name, scopes, allowedServices, allowedNodes, allowAllServices, allowAllNodes);
        return await _client.UpdateApiKeyAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }

    private static Dictionary<string, object?> BuildKeyPayload(
        string? name, string? scopes,
        string? allowedServices, string? allowedNodes,
        bool? allowAllServices, bool? allowAllNodes)
    {
        var payload = new Dictionary<string, object?>();
        if (name != null) payload["name"] = name;
        if (scopes != null) payload["scopes"] = scopes;
        if (allowedServices != null)
            payload["allowed_service_ids"] = allowedServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedNodes != null)
            payload["allowed_node_ids"] = allowedNodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowAllServices.HasValue) payload["allow_all_services"] = allowAllServices.Value;
        if (allowAllNodes.HasValue) payload["allow_all_nodes"] = allowAllNodes.Value;
        return payload;
    }
}
