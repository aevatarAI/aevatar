using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage user's connected services in NyxID.</summary>
public sealed class NyxIdServicesTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdServicesAction> ActionParser = new(
    [
        new("list", NyxIdServicesAction.List, new(false, true, false)),
        new("show", NyxIdServicesAction.Show, new(false, true, false)),
        new("create", NyxIdServicesAction.Create, new(true, false, false)),
        new("update", NyxIdServicesAction.Update, new(true, false, false)),
        new("route", NyxIdServicesAction.Route, new(true, false, false)),
        new("delete", NyxIdServicesAction.Delete, new(true, false, true)),
        new("rotate_credential", NyxIdServicesAction.RotateCredential, new(true, false, true)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdServicesTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_services";

    public string Description =>
        "Manage the user's connected services in NyxID. " +
        "Actions: list, show, create, update, delete, rotate_credential, route.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
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
            },
            "org": {
              "type": "string",
              "description": "Create this service under the given org ID (for create)"
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson) =>
        NyxIdManagedToolReceiptFactory.TryCreate(callId, toolName, resultJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var parsed = ActionParser.Parse(argumentsJson);
        if (!parsed.IsValid)
            return NyxIdClosedActionParser<NyxIdServicesAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdServicesAction.Show when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetServiceAsync(token, id, ct),
            NyxIdServicesAction.Delete when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteServiceAsync(token, id, ct),
            NyxIdServicesAction.Create => await CreateServiceAsync(token, args, ct),
            NyxIdServicesAction.Update when !string.IsNullOrWhiteSpace(id) =>
                await UpdateServiceAsync(token, id, args, ct),
            NyxIdServicesAction.RotateCredential when !string.IsNullOrWhiteSpace(id) =>
                await RotateCredentialAsync(token, id, args, ct),
            NyxIdServicesAction.Route when !string.IsNullOrWhiteSpace(id) =>
                await RouteServiceAsync(token, id, args, ct),

            NyxIdServicesAction.Show or
            NyxIdServicesAction.Delete or
            NyxIdServicesAction.Update or
            NyxIdServicesAction.RotateCredential or
            NyxIdServicesAction.Route =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\",\"received\":{args.Raw}}}",
            NyxIdServicesAction.List => await _client.ListServicesAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdServicesAction>.InvalidActionJson,
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
        var org = args.Str("org");
        if (!string.IsNullOrWhiteSpace(org)) payload["target_org_id"] = org;

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
        catch (JsonException)
        {
            return """{"error":"NyxID returned an invalid service response"}""";
        }

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

internal enum NyxIdServicesAction
{
    List,
    Show,
    Create,
    Update,
    Route,
    Delete,
    RotateCredential,
}
