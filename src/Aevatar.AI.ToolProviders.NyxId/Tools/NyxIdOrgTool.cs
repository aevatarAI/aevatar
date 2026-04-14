using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdOrgTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdOrgTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_orgs";

    public string Description =>
        "Manage NyxID organizations (shared credentials across multiple users). " +
        "Org actions: list, show, create, update, delete, join, set_primary. " +
        "Member actions: list_members, add_member, update_member, remove_member. " +
        "Invite actions: list_invites, create_invite, cancel_invite.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "show", "create", "update", "delete", "join", "set_primary", "list_members", "add_member", "update_member", "remove_member", "list_invites", "create_invite", "cancel_invite"],
              "description": "Action to perform (default: list)"
            },
            "org_id": {
              "type": "string",
              "description": "Organization ID (for show/update/delete/member/invite actions)"
            },
            "display_name": {
              "type": "string",
              "description": "Display name (for create/update)"
            },
            "contact_email": {
              "type": "string",
              "description": "Contact email (for create)"
            },
            "avatar_url": {
              "type": "string",
              "description": "Avatar URL (for create/update, empty string to clear)"
            },
            "nonce": {
              "type": "string",
              "description": "Invite nonce or join URL (for join)"
            },
            "clear": {
              "type": "boolean",
              "description": "Clear primary org (for set_primary)"
            },
            "user_id": {
              "type": "string",
              "description": "User ID (for add_member)"
            },
            "member_id": {
              "type": "string",
              "description": "Member user ID (for update_member/remove_member)"
            },
            "role": {
              "type": "string",
              "enum": ["admin", "member", "viewer"],
              "description": "Member role (for add_member/update_member/create_invite)"
            },
            "allowed_service_ids": {
              "type": "string",
              "description": "Comma-separated service IDs to scope member access (for add_member/update_member/create_invite)"
            },
            "invite_id": {
              "type": "string",
              "description": "Invite ID (for cancel_invite)"
            },
            "ttl_hours": {
              "type": "integer",
              "description": "Invite time-to-live in hours, 1-720 (for create_invite, default: 24)"
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
        var orgId = args.Str("org_id");

        return action switch
        {
            "show" when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.GetOrgAsync(token, orgId, ct),
            "create" => await CreateOrgAsync(token, args, ct),
            "update" when !string.IsNullOrWhiteSpace(orgId) =>
                await UpdateOrgAsync(token, orgId, args, ct),
            "delete" when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.DeleteOrgAsync(token, orgId, ct),
            "join" => await JoinOrgAsync(token, args, ct),
            "set_primary" => await SetPrimaryOrgAsync(token, args, ct),

            "list_members" when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.ListOrgMembersAsync(token, orgId, ct),
            "add_member" when !string.IsNullOrWhiteSpace(orgId) =>
                await AddMemberAsync(token, orgId, args, ct),
            "update_member" when !string.IsNullOrWhiteSpace(orgId) =>
                await UpdateMemberAsync(token, orgId, args, ct),
            "remove_member" when !string.IsNullOrWhiteSpace(orgId) =>
                await RemoveMemberAsync(token, orgId, args, ct),

            "list_invites" when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.ListOrgInvitesAsync(token, orgId, ct),
            "create_invite" when !string.IsNullOrWhiteSpace(orgId) =>
                await CreateInviteAsync(token, orgId, args, ct),
            "cancel_invite" when !string.IsNullOrWhiteSpace(orgId) =>
                await CancelInviteAsync(token, orgId, args, ct),

            "show" or "update" or "delete" or "list_members" or "add_member" or
            "update_member" or "remove_member" or "list_invites" or "create_invite" or "cancel_invite" =>
                $"{{\"error\":\"'org_id' is required for {action}\"}}",

            _ => await _client.ListOrgsAsync(token, ct),
        };
    }

    private async Task<string> CreateOrgAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var name = args.Str("display_name");
        if (string.IsNullOrWhiteSpace(name))
            return """{"error":"'display_name' is required for create"}""";

        var payload = new Dictionary<string, object?> { ["display_name"] = name };
        var email = args.Str("contact_email");
        if (!string.IsNullOrWhiteSpace(email)) payload["contact_email"] = email;
        var avatar = args.Str("avatar_url");
        if (avatar != null) payload["avatar_url"] = avatar;

        return await _client.CreateOrgAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateOrgAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        var name = args.Str("display_name");
        if (name != null) payload["display_name"] = name;
        var avatar = args.Str("avatar_url");
        if (avatar != null) payload["avatar_url"] = avatar;

        if (payload.Count == 0)
            return """{"error":"Provide 'display_name' or 'avatar_url' to update"}""";

        return await _client.UpdateOrgAsync(token, orgId, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> JoinOrgAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var nonce = args.Str("nonce");
        if (string.IsNullOrWhiteSpace(nonce))
            return """{"error":"'nonce' is required for join"}""";

        var trimmed = nonce.Trim();
        var joinIdx = trimmed.LastIndexOf("/orgs/join/", StringComparison.OrdinalIgnoreCase);
        if (joinIdx >= 0)
            trimmed = trimmed[(joinIdx + "/orgs/join/".Length)..].Split('?', '#')[0];

        return await _client.JoinOrgAsync(token, trimmed, ct);
    }

    private async Task<string> SetPrimaryOrgAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var orgId = args.Str("org_id");
        var clear = args.Bool("clear") == true;

        string body;
        if (clear)
            body = """{"primary_org_id":null}""";
        else if (!string.IsNullOrWhiteSpace(orgId))
            body = JsonSerializer.Serialize(new { primary_org_id = orgId });
        else
            return """{"error":"Provide 'org_id' to set, or 'clear: true' to unset primary org"}""";

        return await _client.SetPrimaryOrgAsync(token, body, ct);
    }

    private async Task<string> AddMemberAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var userId = args.Str("user_id");
        var role = args.Str("role", "member");
        if (string.IsNullOrWhiteSpace(userId))
            return """{"error":"'user_id' is required for add_member"}""";

        var payload = new Dictionary<string, object?> { ["user_id"] = userId, ["role"] = role };
        var serviceIds = args.Str("allowed_service_ids");
        if (!string.IsNullOrWhiteSpace(serviceIds))
            payload["allowed_service_ids"] = serviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return await _client.AddOrgMemberAsync(token, orgId, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> UpdateMemberAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var memberId = args.Str("member_id");
        if (string.IsNullOrWhiteSpace(memberId))
            return """{"error":"'member_id' is required for update_member"}""";

        var payload = new Dictionary<string, object?>();
        var role = args.Str("role");
        if (role != null) payload["role"] = role;
        var serviceIds = args.Str("allowed_service_ids");
        if (serviceIds != null)
        {
            if (string.IsNullOrEmpty(serviceIds))
                payload["allowed_service_ids"] = null;
            else
                payload["allowed_service_ids"] = serviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (payload.Count == 0)
            return """{"error":"Provide 'role' or 'allowed_service_ids' to update"}""";

        return await _client.UpdateOrgMemberAsync(token, orgId, memberId, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> RemoveMemberAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var memberId = args.Str("member_id");
        if (string.IsNullOrWhiteSpace(memberId))
            return """{"error":"'member_id' is required for remove_member"}""";

        return await _client.RemoveOrgMemberAsync(token, orgId, memberId, ct);
    }

    private async Task<string> CreateInviteAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var role = args.Str("role", "member");
        var payload = new Dictionary<string, object?> { ["role"] = role };

        var serviceIds = args.Str("allowed_service_ids");
        if (!string.IsNullOrWhiteSpace(serviceIds))
            payload["allowed_service_ids"] = serviceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var ttlStr = args.Str("ttl_hours");
        if (int.TryParse(ttlStr, out var ttl) && ttl > 0)
            payload["ttl_hours"] = ttl;

        return await _client.CreateOrgInviteAsync(token, orgId, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> CancelInviteAsync(string token, string orgId, ToolArgs args, CancellationToken ct)
    {
        var inviteId = args.Str("invite_id");
        if (string.IsNullOrWhiteSpace(inviteId))
            return """{"error":"'invite_id' is required for cancel_invite"}""";

        return await _client.CancelOrgInviteAsync(token, orgId, inviteId, ct);
    }
}
