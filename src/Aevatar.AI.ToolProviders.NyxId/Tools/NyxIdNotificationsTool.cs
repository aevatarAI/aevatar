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
        "Actions: settings, update, telegram_link, telegram_disconnect.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["settings", "update", "telegram_link", "telegram_disconnect"],
              "description": "Action to perform (default: settings)"
            },
            "approval_email": {
              "type": "boolean",
              "description": "Enable/disable approval email (for update)"
            },
            "approval_push": {
              "type": "boolean",
              "description": "Enable/disable approval push (for update)"
            },
            "approval_telegram": {
              "type": "boolean",
              "description": "Enable/disable approval telegram (for update)"
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
        var action = args.Str("action", "settings");

        return action switch
        {
            "update" => await UpdateAsync(token, args, ct),
            "telegram_link" => await _client.TelegramLinkAsync(token, ct),
            "telegram_disconnect" => await _client.TelegramDisconnectAsync(token, ct),
            _ => await _client.GetNotificationSettingsAsync(token, ct),
        };
    }

    private async Task<string> UpdateAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var p = new Dictionary<string, object?>();
        var ae = args.Bool("approval_email");
        if (ae.HasValue) p["approval_email"] = ae.Value;
        var ap = args.Bool("approval_push");
        if (ap.HasValue) p["push_enabled"] = ap.Value;
        var at = args.Bool("approval_telegram");
        if (at.HasValue) p["telegram_enabled"] = at.Value;
        return await _client.UpdateNotificationSettingsAsync(token, JsonSerializer.Serialize(p), ct);
    }
}
