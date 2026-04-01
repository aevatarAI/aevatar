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
        "Actions: 'list' all services, 'show' details, 'create' a new service, " +
        "'update' service config (label, endpoint, active status), " +
        "'delete' a service, 'rotate_credential' to rotate the external credential, " +
        "'route' to change service routing (node or direct).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "delete", "update", "rotate_credential", "route"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "Service ID (required for 'show', 'delete', 'update', 'rotate_credential', 'route')"
            },
            "service_slug": {
              "type": "string",
              "description": "Catalog service slug (for 'create', e.g. 'telegram-bot', 'openai')"
            },
            "credential": {
              "type": "string",
              "description": "API key or token value (for 'create' or 'rotate_credential')"
            },
            "label": {
              "type": "string",
              "description": "User-friendly label (for 'create' or 'update')"
            },
            "endpoint_url": {
              "type": "string",
              "description": "Endpoint URL (for 'create' or 'update')"
            },
            "node_id": {
              "type": "string",
              "description": "Node ID for routing (for 'update' or 'route')"
            },
            "active": {
              "type": "boolean",
              "description": "Set service active/inactive (for 'update')"
            },
            "direct": {
              "type": "boolean",
              "description": "Use direct routing, no node (for 'route')"
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
        string? serviceSlug = null;
        string? credential = null;
        string? label = null;
        string? endpointUrl = null;
        string? nodeId = null;
        bool? active = null;
        bool direct = false;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("service_slug", out var ss))
                serviceSlug = ss.GetString();
            if (doc.RootElement.TryGetProperty("credential", out var c))
                credential = c.GetString();
            if (doc.RootElement.TryGetProperty("label", out var l))
                label = l.GetString();
            if (doc.RootElement.TryGetProperty("endpoint_url", out var eu))
                endpointUrl = eu.GetString();
            if (doc.RootElement.TryGetProperty("node_id", out var ni))
                nodeId = ni.GetString();
            if (doc.RootElement.TryGetProperty("active", out var ac))
                active = ac.GetBoolean();
            if (doc.RootElement.TryGetProperty("direct", out var d))
                direct = d.GetBoolean();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetServiceAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteServiceAsync(token, id, ct),
            "create" when !string.IsNullOrWhiteSpace(serviceSlug) && !string.IsNullOrWhiteSpace(credential) =>
                await CreateServiceAsync(token, serviceSlug, credential, label, endpointUrl, ct),
            "update" when !string.IsNullOrWhiteSpace(id) =>
                await UpdateServiceAsync(token, id, label, endpointUrl, nodeId, active, ct),
            "rotate_credential" when !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(credential) =>
                await RotateCredentialAsync(token, id, credential, ct),
            "route" when !string.IsNullOrWhiteSpace(id) =>
                await RouteServiceAsync(token, id, nodeId, direct, ct),

            "show" or "delete" or "update" or "rotate_credential" or "route" =>
                "Error: 'id' is required for this action.",
            "create" => "Error: 'service_slug' and 'credential' are required for create.",
            _ => await _client.ListServicesAsync(token, ct),
        };
    }

    private async Task<string> CreateServiceAsync(
        string token, string serviceSlug, string credential, string? label, string? endpointUrl,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["service_slug"] = serviceSlug,
            ["credential"] = credential,
            ["label"] = label ?? serviceSlug,
        };

        if (!string.IsNullOrWhiteSpace(endpointUrl))
            payload["endpoint_url"] = endpointUrl;

        return await _client.CreateServiceAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateServiceAsync(
        string token, string id, string? label, string? endpointUrl, string? nodeId, bool? active,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (label != null) payload["label"] = label;
        if (endpointUrl != null) payload["endpoint_url"] = endpointUrl;
        if (nodeId != null) payload["node_id"] = nodeId;
        if (active.HasValue) payload["is_active"] = active.Value;

        return await _client.UpdateServiceAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> RotateCredentialAsync(
        string token, string id, string credential, CancellationToken ct)
    {
        // Step 1: Get service details to find the external api_key_id
        var serviceJson = await _client.GetServiceAsync(token, id, ct);
        string? apiKeyId = null;
        try
        {
            using var doc = JsonDocument.Parse(serviceJson);
            if (doc.RootElement.TryGetProperty("api_key_id", out var ak))
                apiKeyId = ak.GetString();
        }
        catch { /* ignore parse errors */ }

        if (string.IsNullOrWhiteSpace(apiKeyId))
            return "Error: Could not find api_key_id for this service. The service may not have an external credential.";

        // Step 2: Update the external key with new credential
        var body = JsonSerializer.Serialize(new { credential });
        return await _client.UpdateExternalKeyAsync(token, apiKeyId, body, ct);
    }

    private async Task<string> RouteServiceAsync(
        string token, string id, string? nodeId, bool direct, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (direct)
            payload["node_id"] = null;
        else if (!string.IsNullOrWhiteSpace(nodeId))
            payload["node_id"] = nodeId;
        else
            return "Error: Either 'node_id' or 'direct: true' is required for route action.";

        return await _client.UpdateServiceAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }
}
