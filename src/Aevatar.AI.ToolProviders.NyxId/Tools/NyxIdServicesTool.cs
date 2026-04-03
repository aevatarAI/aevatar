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
        "Actions: list, show, create, update, delete, rotate_credential, route.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "delete", "update", "rotate_credential", "route"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Service ID (for show/delete/update/rotate_credential/route)"
            },
            "service_slug": {
              "type": "string",
              "description": "Catalog slug (for create)"
            },
            "credential": {
              "type": "string",
              "description": "API key or token (for create or rotate_credential)"
            },
            "label": {
              "type": "string",
              "description": "Label (for create or update)"
            },
            "endpoint_url": {
              "type": "string",
              "description": "Endpoint URL (for create or update)"
            },
            "node_id": {
              "type": "string",
              "description": "Node ID for routing (for update or route)"
            },
            "active": {
              "type": "boolean",
              "description": "Set active/inactive (for update)"
            },
            "direct": {
              "type": "boolean",
              "description": "Use direct routing (for route)"
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
                await _client.GetServiceAsync(token, id, ct),
            "delete" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteServiceAsync(token, id, ct),
            "create" => await CreateServiceAsync(token, args, ct),
            "update" when !string.IsNullOrWhiteSpace(id) =>
                await UpdateServiceAsync(token, id, args, ct),
            "rotate_credential" when !string.IsNullOrWhiteSpace(id) =>
                await RotateCredentialAsync(token, id, args, ct),
            "route" when !string.IsNullOrWhiteSpace(id) =>
                await RouteServiceAsync(token, id, args, ct),

            "show" or "delete" or "update" or "rotate_credential" or "route" =>
                $"{{\"error\":\"'id' is required for {action}\",\"received\":{args.Raw}}}",
            _ => await _client.ListServicesAsync(token, ct),
        };
    }

    private async Task<string> CreateServiceAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var slug = args.Str("service_slug") ?? args.Str("slug");
        var credential = args.Str("credential");
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(credential))
            return $"{{\"error\":\"'service_slug' and 'credential' required for create\",\"received\":{args.Raw}}}";

        var payload = new Dictionary<string, object?>
        {
            ["service_slug"] = slug,
            ["credential"] = credential,
            ["label"] = args.Str("label") ?? slug,
        };
        var url = args.Str("endpoint_url");
        if (!string.IsNullOrWhiteSpace(url)) payload["endpoint_url"] = url;

        return await _client.CreateServiceAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateServiceAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        var label = args.Str("label");
        if (label != null) payload["label"] = label;
        var url = args.Str("endpoint_url");
        if (url != null) payload["endpoint_url"] = url;
        var nodeId = args.Str("node_id");
        if (nodeId != null) payload["node_id"] = nodeId;
        var active = args.Bool("active");
        if (active.HasValue) payload["is_active"] = active.Value;

        return await _client.UpdateServiceAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> RotateCredentialAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var credential = args.Str("credential");
        if (string.IsNullOrWhiteSpace(credential))
            return """{"error":"'credential' is required for rotate_credential"}""";

        var serviceJson = await _client.GetServiceAsync(token, id, ct);
        string? apiKeyId = null;
        try
        {
            using var doc = JsonDocument.Parse(serviceJson);
            if (doc.RootElement.TryGetProperty("api_key_id", out var ak))
                apiKeyId = ak.GetString();
        }
        catch { /* ignore */ }

        if (string.IsNullOrWhiteSpace(apiKeyId))
            return """{"error":"Could not find api_key_id for this service"}""";

        return await _client.UpdateExternalKeyAsync(token, apiKeyId,
            JsonSerializer.Serialize(new { credential }), ct);
    }

    private async Task<string> RouteServiceAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (args.Bool("direct") == true)
            payload["node_id"] = string.Empty;
        else if (!string.IsNullOrWhiteSpace(args.Str("node_id")))
            payload["node_id"] = args.Str("node_id");
        else
            return """{"error":"Either 'node_id' or 'direct: true' is required for route"}""";

        return await _client.UpdateServiceAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }
}
