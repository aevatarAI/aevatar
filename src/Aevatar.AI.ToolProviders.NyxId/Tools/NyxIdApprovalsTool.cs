using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID approval requests, grants, and settings.</summary>
public sealed class NyxIdApprovalsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdApprovalsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_approvals";

    public string Description =>
        "Manage approval requests for proxied service calls. " +
        "Actions: list, show, approve, deny, configs, grants, revoke_grant, enable, disable, set_config.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "approve", "deny", "configs", "grants", "revoke_grant", "enable", "disable", "set_config"],
              "description": "Action to perform (default: list)"
            },
            "id": {
              "type": "string",
              "description": "Request/grant/service-config ID"
            },
            "require_approval": {
              "type": "boolean",
              "description": "For set_config: require approval"
            },
            "approval_mode": {
              "type": "string",
              "enum": ["per_request", "grant"],
              "description": "For set_config: approval mode"
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
                await _client.GetApprovalAsync(token, id, ct),
            "approve" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, """{"decision":"approve"}""", ct),
            "deny" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, """{"decision":"deny"}""", ct),
            "revoke_grant" when !string.IsNullOrWhiteSpace(id) =>
                await _client.RevokeApprovalGrantAsync(token, id, ct),
            "set_config" when !string.IsNullOrWhiteSpace(id) =>
                await SetConfigAsync(token, id, args, ct),

            "show" or "approve" or "deny" or "revoke_grant" or "set_config" =>
                $"{{\"error\":\"'id' is required for {action}\"}}",

            "grants" => await _client.ListApprovalGrantsAsync(token, ct),
            "configs" => await _client.ListApprovalServiceConfigsAsync(token, ct),
            "enable" => await _client.SetGlobalApprovalAsync(token, true, ct),
            "disable" => await _client.SetGlobalApprovalAsync(token, false, ct),
            _ => await _client.ListApprovalsAsync(token, ct),
        };
    }

    private async Task<string> SetConfigAsync(string token, string id, ToolArgs args, CancellationToken ct)
    {
        var p = new Dictionary<string, object?>();
        var ra = args.Bool("require_approval");
        if (ra.HasValue) p["require_approval"] = ra.Value;
        var mode = args.Str("approval_mode");
        if (mode != null) p["approval_mode"] = mode;
        return await _client.SetApprovalConfigAsync(token, id, JsonSerializer.Serialize(p), ct);
    }
}
