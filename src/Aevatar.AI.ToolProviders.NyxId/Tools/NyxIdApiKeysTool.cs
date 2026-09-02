using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID API keys for programmatic access.</summary>
public sealed class NyxIdApiKeysTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdApiKeysAction> ActionParser = new(
    [
        new("list", NyxIdApiKeysAction.List, new(false, true, false)),
        new("show", NyxIdApiKeysAction.Show, new(false, true, false)),
        new("create", NyxIdApiKeysAction.Create, new(true, false, false)),
        new("rotate", NyxIdApiKeysAction.Rotate, new(true, false, true)),
        new("delete", NyxIdApiKeysAction.Delete, new(true, false, true)),
        new("update", NyxIdApiKeysAction.Update, new(true, false, false)),
        new("bind", NyxIdApiKeysAction.Bind, new(true, false, false)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdApiKeysTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_api_keys";

    public string Description =>
        "Manage NyxID API keys. " +
        "Actions: list, show, create, rotate, delete, update, bind.";

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
              "description": "API key ID (for show/rotate/delete/update)"
            },
            "name": {
              "type": "string",
              "description": "Key name (required for create)"
            },
            "scopes": {
              "type": "string",
              "description": "Space-separated scopes (e.g. 'proxy read write')"
            },
            "allowed_services": {
              "type": "string",
              "description": "Comma-separated service IDs"
            },
            "allowed_nodes": {
              "type": "string",
              "description": "Comma-separated node IDs"
            },
            "allow_all_services": {
              "type": "boolean",
              "description": "Allow all services"
            },
            "allow_all_nodes": {
              "type": "boolean",
              "description": "Allow all nodes"
            },
            "callback_url": {
              "type": "string",
              "description": "Webhook callback URL for channel bot relay (for create or update)"
            },
            "platform": {
              "type": "string",
              "description": "Platform label: claude-code, codex, openclaw, cursor, generic (for create)"
            },
            "expires_in_days": {
              "type": "integer",
              "description": "Expiry in days, 0 = no expiry (for create)"
            },
            "org": {
              "type": "string",
              "description": "Create key under this org ID (for create or list)"
            },
            "service_slug": {
              "type": "string",
              "description": "Service slug to bind (for bind)"
            },
            "credential_label": {
              "type": "string",
              "description": "External credential label (for bind, auto-resolved if omitted)"
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var parsed = ActionParser.Parse(argumentsJson);
        if (!parsed.IsValid)
            return NyxIdClosedActionParser<NyxIdApiKeysAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var id = args.Str("id");

        return parsed.Action switch
        {
            NyxIdApiKeysAction.Show when !string.IsNullOrWhiteSpace(id) =>
                await _client.GetApiKeyAsync(token, id, ct),
            NyxIdApiKeysAction.Rotate when !string.IsNullOrWhiteSpace(id) =>
                await _client.RotateApiKeyAsync(token, id, ct),
            NyxIdApiKeysAction.Delete when !string.IsNullOrWhiteSpace(id) =>
                await _client.DeleteApiKeyAsync(token, id, ct),
            NyxIdApiKeysAction.Update when !string.IsNullOrWhiteSpace(id) =>
                await UpdateKeyAsync(token, id, args, ct),
            NyxIdApiKeysAction.Create => await CreateKeyAsync(token, args, ct),
            NyxIdApiKeysAction.Bind when !string.IsNullOrWhiteSpace(id) =>
                await BindKeyAsync(token, id, args, ct),

            NyxIdApiKeysAction.Show or
            NyxIdApiKeysAction.Rotate or
            NyxIdApiKeysAction.Delete or
            NyxIdApiKeysAction.Update or
            NyxIdApiKeysAction.Bind =>
                $"{{\"error\":\"'id' is required for {parsed.Name}\"}}",
            NyxIdApiKeysAction.List => await ListKeysAsync(token, args, ct),
            _ => NyxIdClosedActionParser<NyxIdApiKeysAction>.InvalidActionJson,
        };
    }

    private async Task<string> ListKeysAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var org = args.Str("org");
        if (!string.IsNullOrWhiteSpace(org))
            return await _client.GetAsync(token, $"/api/v1/api-keys?org_id={Uri.EscapeDataString(org)}", ct);
        return await _client.ListApiKeysAsync(token, ct);
    }

    private async Task<string> CreateKeyAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'name' is required for create"}""";
        if (RejectInsecureCallbackUrl(args) is { } callbackError)
            return callbackError;
        return await _client.CreateApiKeyAsync(token, JsonSerializer.Serialize(BuildPayload(args, name)), ct);
    }

    private async Task<string> UpdateKeyAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        if (RejectInsecureCallbackUrl(args) is { } callbackError)
            return callbackError;
        return await _client.UpdateApiKeyAsync(token, id, JsonSerializer.Serialize(BuildPayload(args, args.Str("name"))), ct);
    }

    // A channel-bot relay callback_url receives NyxID's inbound relay callback carrying the
    // short-lived X-NyxID-User-Token. Refuse to register a cleartext callback on this agent-facing
    // key create/update path — the same fail-closed policy the Lark/Telegram provisioning services
    // apply — so it cannot be used (via nyxid_api_keys create/update + nyxid_channel_bots
    // create_route) to route that credential to a cleartext public host. Empty is left to NyxID.
    private static string? RejectInsecureCallbackUrl(ToolArgs args)
    {
        var callbackUrl = args.Str("callback_url");
        if (string.IsNullOrWhiteSpace(callbackUrl) || NyxRelayCallbackUrlPolicy.IsSecureUrl(callbackUrl))
            return null;
        return """{"error":"callback_url must be https (or http only for loopback hosts); refusing to register a cleartext callback that would leak the relay user token."}""";
    }

    private async Task<string> BindKeyAsync(string token, string keyId, ToolArgs args, CancellationToken ct)
    {
        var serviceSlug = args.Str("service_slug");
        if (string.IsNullOrWhiteSpace(serviceSlug))
            return """{"error":"'service_slug' is required for bind"}""";

        var servicesJson = await _client.ListServicesAsync(token, ct);
        string? userServiceId = null;
        string? userApiKeyId = null;
        try
        {
            using var doc = JsonDocument.Parse(servicesJson);
            if (doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var svc in keysArr.EnumerateArray())
                {
                    if (svc.TryGetProperty("slug", out var slug) && slug.GetString() == serviceSlug)
                    {
                        if (svc.TryGetProperty("id", out var idProp))
                            userServiceId = idProp.GetString();
                        if (svc.TryGetProperty("api_key_id", out var akProp))
                            userApiKeyId = akProp.GetString();
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return """{"error":"invalid_services_response"}""";
        }

        if (string.IsNullOrWhiteSpace(userServiceId))
            return $"{{\"error\":\"Service '{serviceSlug}' not found\"}}";

        var credLabel = args.Str("credential_label");
        if (!string.IsNullOrWhiteSpace(credLabel))
        {
            var extKeysJson = await _client.ListExternalKeysAsync(token, ct);
            try
            {
                using var doc = JsonDocument.Parse(extKeysJson);
                if (doc.RootElement.TryGetProperty("api_keys", out var extArr) && extArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in extArr.EnumerateArray())
                    {
                        var label = k.TryGetProperty("label", out var lp) ? lp.GetString() : null;
                        var name = k.TryGetProperty("name", out var np) ? np.GetString() : null;
                        if (label == credLabel || name == credLabel)
                        {
                            if (k.TryGetProperty("id", out var idp))
                                userApiKeyId = idp.GetString();
                            break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return """{"error":"invalid_external_keys_response"}""";
            }
        }

        if (string.IsNullOrWhiteSpace(userApiKeyId))
            return $"{{\"error\":\"No credential found for service '{serviceSlug}'. Add a credential first or specify 'credential_label'.\"}}";

        var body = JsonSerializer.Serialize(new { user_service_id = userServiceId, user_api_key_id = userApiKeyId });
        return await _client.BindApiKeyAsync(token, keyId, body, ct);
    }

    private static Dictionary<string, object?> BuildPayload(ToolArgs args, string? name)
    {
        var p = new Dictionary<string, object?>();
        if (name != null) p["name"] = name;
        var scopes = args.Str("scopes");
        if (scopes != null) p["scopes"] = scopes;
        var svc = args.Str("allowed_services");
        if (svc != null) p["allowed_service_ids"] = svc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nodes = args.Str("allowed_nodes");
        if (nodes != null) p["allowed_node_ids"] = nodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var aas = args.Bool("allow_all_services");
        if (aas.HasValue) p["allow_all_services"] = aas.Value;
        var aan = args.Bool("allow_all_nodes");
        if (aan.HasValue) p["allow_all_nodes"] = aan.Value;
        var callbackUrl = args.Str("callback_url");
        if (callbackUrl != null) p["callback_url"] = callbackUrl;
        var platform = args.Str("platform");
        if (platform != null) p["platform"] = platform;
        var org = args.Str("org");
        if (org != null) p["target_org_id"] = org;
        var expiresStr = args.Str("expires_in_days");
        if (int.TryParse(expiresStr, out var days) && days > 0)
            p["expires_at"] = DateTime.UtcNow.AddDays(days).ToString("o");
        return p;
    }
}

internal enum NyxIdApiKeysAction
{
    List,
    Show,
    Create,
    Rotate,
    Delete,
    Update,
    Bind,
}
