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
        "Actions: 'list' pending approvals, 'show' request details, " +
        "'approve'/'deny' a request, 'configs' for per-service settings, " +
        "'grants' to list approval grants, 'revoke_grant' to revoke a grant, " +
        "'enable'/'disable' global approval protection, " +
        "'set_config' to configure per-service approval settings.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "approve", "deny", "configs", "grants", "revoke_grant", "enable", "disable", "set_config"],
              "description": "Action to perform"
            },
            "id": {
              "type": "string",
              "description": "Approval request ID (for 'show', 'approve', 'deny'), grant ID (for 'revoke_grant'), or service config ID (for 'set_config')"
            },
            "require_approval": {
              "type": "boolean",
              "description": "Whether to require approval for this service (for 'set_config')"
            },
            "approval_mode": {
              "type": "string",
              "enum": ["per_request", "grant"],
              "description": "Approval mode: 'per_request' (every call needs approval) or 'grant' (time-based grant). For 'set_config'."
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
        bool? requireApproval = null;
        string? approvalMode = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
            if (doc.RootElement.TryGetProperty("require_approval", out var ra))
                requireApproval = ra.GetBoolean();
            if (doc.RootElement.TryGetProperty("approval_mode", out var am))
                approvalMode = am.GetString();
        }
        catch { /* use defaults */ }

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
                await SetApprovalConfigAsync(token, id, requireApproval, approvalMode, ct),

            "show" or "approve" or "deny" or "revoke_grant" or "set_config" =>
                $"Error: 'id' is required for {action} action.",

            "grants" => await _client.ListApprovalGrantsAsync(token, ct),
            "configs" => await _client.ListApprovalServiceConfigsAsync(token, ct),
            "enable" => await _client.UpdateNotificationSettingsAsync(token,
                """{"approval_required":true}""", ct),
            "disable" => await _client.UpdateNotificationSettingsAsync(token,
                """{"approval_required":false}""", ct),
            _ => await _client.ListApprovalsAsync(token, ct),
        };
    }

    private async Task<string> SetApprovalConfigAsync(
        string token, string id, bool? requireApproval, string? approvalMode,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (requireApproval.HasValue) payload["require_approval"] = requireApproval.Value;
        if (approvalMode != null) payload["approval_mode"] = approvalMode;
        return await _client.SetApprovalConfigAsync(token, id, JsonSerializer.Serialize(payload), ct);
    }
}
