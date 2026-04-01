using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID notification settings and Telegram integration.</summary>
public sealed class NyxIdNotificationsTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdNotificationsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_notifications";

    public string Description =>
        "Manage notification settings and Telegram integration. " +
        "Actions: 'settings' to view current settings, " +
        "'update' to change notification preferences (email, push, telegram for approvals), " +
        "'telegram_link' to generate a Telegram account link code, " +
        "'telegram_disconnect' to disconnect Telegram.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["settings", "update", "telegram_link", "telegram_disconnect"],
              "description": "Action to perform"
            },
            "approval_email": {
              "type": "boolean",
              "description": "Enable/disable approval email notifications (for 'update')"
            },
            "approval_push": {
              "type": "boolean",
              "description": "Enable/disable approval push notifications (for 'update')"
            },
            "approval_telegram": {
              "type": "boolean",
              "description": "Enable/disable approval Telegram notifications (for 'update')"
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

        string action = "settings";
        bool? approvalEmail = null;
        bool? approvalPush = null;
        bool? approvalTelegram = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "settings";
            if (doc.RootElement.TryGetProperty("approval_email", out var ae))
                approvalEmail = ae.GetBoolean();
            if (doc.RootElement.TryGetProperty("approval_push", out var ap))
                approvalPush = ap.GetBoolean();
            if (doc.RootElement.TryGetProperty("approval_telegram", out var at))
                approvalTelegram = at.GetBoolean();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "update" => await UpdateSettingsAsync(token, approvalEmail, approvalPush, approvalTelegram, ct),
            "telegram_link" => await _client.TelegramLinkAsync(token, ct),
            "telegram_disconnect" => await _client.TelegramDisconnectAsync(token, ct),
            _ => await _client.GetNotificationSettingsAsync(token, ct),
        };
    }

    private async Task<string> UpdateSettingsAsync(
        string token, bool? approvalEmail, bool? approvalPush, bool? approvalTelegram,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>();
        if (approvalEmail.HasValue) payload["approval_email"] = approvalEmail.Value;
        if (approvalPush.HasValue) payload["push_enabled"] = approvalPush.Value;
        if (approvalTelegram.HasValue) payload["telegram_enabled"] = approvalTelegram.Value;
        return await _client.UpdateNotificationSettingsAsync(token, JsonSerializer.Serialize(payload), ct);
    }
}
