using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID approval requests.</summary>
public sealed class NyxIdApprovalsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdApprovalsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_approvals";

    public string Description =>
        "Manage approval requests for proxied service calls. " +
        "Use 'list' to see pending approvals, 'approve' or 'deny' to decide on a request, " +
        "or 'configs' to view per-service approval settings.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "approve", "deny", "configs"],
              "description": "Action: 'list' pending approvals, 'approve'/'deny' a request, or 'configs' for service approval settings"
            },
            "id": {
              "type": "string",
              "description": "Approval request ID (required for 'approve' and 'deny')"
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

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "list";
            if (doc.RootElement.TryGetProperty("id", out var i))
                id = i.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "approve" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, """{"decision":"approve"}""", ct),
            "deny" when !string.IsNullOrWhiteSpace(id) =>
                await _client.DecideApprovalAsync(token, id, """{"decision":"deny"}""", ct),
            "approve" or "deny" => $"Error: 'id' is required for {action} action.",
            "configs" => await _client.ListApprovalServiceConfigsAsync(token, ct),
            _ => await _client.ListApprovalsAsync(token, ct),
        };
    }
}
