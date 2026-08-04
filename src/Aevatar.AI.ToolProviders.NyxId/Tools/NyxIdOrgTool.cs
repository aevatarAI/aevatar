using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdOrgTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdOrgAction> ActionParser = new(
    [
        new("list", NyxIdOrgAction.List, new(false, true, false)),
        new("show", NyxIdOrgAction.Show, new(false, true, false)),
        new("create", NyxIdOrgAction.Create, new(true, false, false)),
        new("update", NyxIdOrgAction.Update, new(true, false, false)),
        new("delete", NyxIdOrgAction.Delete, new(true, false, true)),
        new("join", NyxIdOrgAction.Join, new(true, false, false)),
        new("set_primary", NyxIdOrgAction.SetPrimary, new(true, false, false)),
        new("list_members", NyxIdOrgAction.ListMembers, new(false, true, false)),
        new("add_member", NyxIdOrgAction.AddMember, new(true, false, false)),
        new("update_member", NyxIdOrgAction.UpdateMember, new(true, false, false)),
        new("remove_member", NyxIdOrgAction.RemoveMember, new(true, false, true)),
        new("list_invites", NyxIdOrgAction.ListInvites, new(false, true, false)),
        new("create_invite", NyxIdOrgAction.CreateInvite, new(true, false, false)),
        new("cancel_invite", NyxIdOrgAction.CancelInvite, new(true, false, true)),
    ]);

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdOrgTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_orgs";

    public string Description =>
        "Manage NyxID organizations (shared credentials across multiple users). " +
        "Org actions: list, show, create, update, delete, join, set_primary. " +
        "Member actions: list_members, add_member, update_member, remove_member. " +
        "Invite actions: list_invites, create_invite, cancel_invite.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
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
            return NyxIdClosedActionParser<NyxIdOrgAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        var orgId = args.Str("org_id");

        return parsed.Action switch
        {
            NyxIdOrgAction.Show when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.GetOrgAsync(token, orgId, ct),
            NyxIdOrgAction.Create => await CreateOrgAsync(token, args, ct),
            NyxIdOrgAction.Update when !string.IsNullOrWhiteSpace(orgId) =>
                await UpdateOrgAsync(token, orgId, args, ct),
            NyxIdOrgAction.Delete when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.DeleteOrgAsync(token, orgId, ct),
            NyxIdOrgAction.Join => await JoinOrgAsync(token, args, ct),
            NyxIdOrgAction.SetPrimary => await SetPrimaryOrgAsync(token, args, ct),

            NyxIdOrgAction.ListMembers when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.ListOrgMembersAsync(token, orgId, ct),
            NyxIdOrgAction.AddMember when !string.IsNullOrWhiteSpace(orgId) =>
                await AddMemberAsync(token, orgId, args, ct),
            NyxIdOrgAction.UpdateMember when !string.IsNullOrWhiteSpace(orgId) =>
                await UpdateMemberAsync(token, orgId, args, ct),
            NyxIdOrgAction.RemoveMember when !string.IsNullOrWhiteSpace(orgId) =>
                await RemoveMemberAsync(token, orgId, args, ct),

            NyxIdOrgAction.ListInvites when !string.IsNullOrWhiteSpace(orgId) =>
                await _client.ListOrgInvitesAsync(token, orgId, ct),
            NyxIdOrgAction.CreateInvite when !string.IsNullOrWhiteSpace(orgId) =>
                await CreateInviteAsync(token, orgId, args, ct),
            NyxIdOrgAction.CancelInvite when !string.IsNullOrWhiteSpace(orgId) =>
                await CancelInviteAsync(token, orgId, args, ct),

            NyxIdOrgAction.Show or
            NyxIdOrgAction.Update or
            NyxIdOrgAction.Delete or
            NyxIdOrgAction.ListMembers or
            NyxIdOrgAction.AddMember or
            NyxIdOrgAction.UpdateMember or
            NyxIdOrgAction.RemoveMember or
            NyxIdOrgAction.ListInvites or
            NyxIdOrgAction.CreateInvite or
            NyxIdOrgAction.CancelInvite =>
                $"{{\"error\":\"'org_id' is required for {parsed.Name}\"}}",

            NyxIdOrgAction.List => await _client.ListOrgsAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdOrgAction>.InvalidActionJson,
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

internal enum NyxIdOrgAction
{
    List,
    Show,
    Create,
    Update,
    Delete,
    Join,
    SetPrimary,
    ListMembers,
    AddMember,
    UpdateMember,
    RemoveMember,
    ListInvites,
    CreateInvite,
    CancelInvite,
}
