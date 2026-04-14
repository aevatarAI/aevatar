using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdAdminTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdAdminTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_admin";

    public string Description =>
        "NyxID administrative commands (admin role required). " +
        "Actions: list_invite_codes, create_invite_code, deactivate_invite_code.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_invite_codes", "create_invite_code", "deactivate_invite_code"],
              "description": "Action to perform (default: list_invite_codes)"
            },
            "id": {
              "type": "string",
              "description": "Invite code ID (for deactivate_invite_code)"
            },
            "max_uses": {
              "type": "integer",
              "description": "Maximum registrations this code can grant, 1-1000 (for create_invite_code, default: 10)"
            },
            "note": {
              "type": "string",
              "description": "Admin note describing intended recipient (for create_invite_code)"
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
        var action = args.Str("action", "list_invite_codes");

        return action switch
        {
            "create_invite_code" => await CreateInviteCodeAsync(token, args, ct),
            "deactivate_invite_code" => await DeactivateInviteCodeAsync(token, args, ct),
            _ => await _client.ListInviteCodesAsync(token, ct),
        };
    }

    private async Task<string> CreateInviteCodeAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        var maxUsesStr = args.Str("max_uses");
        if (int.TryParse(maxUsesStr, out var maxUses) && maxUses > 0)
            payload["max_uses"] = maxUses;
        var note = args.Str("note");
        if (!string.IsNullOrWhiteSpace(note))
            payload["note"] = note;

        return await _client.CreateInviteCodeAsync(token, JsonSerializer.Serialize(payload), ct);
    }

    private async Task<string> DeactivateInviteCodeAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var id = args.Str("id");
        if (string.IsNullOrWhiteSpace(id))
            return """{"error":"'id' is required for deactivate_invite_code"}""";

        return await _client.DeactivateInviteCodeAsync(token, id, ct);
    }
}
