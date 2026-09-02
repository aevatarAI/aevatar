using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID notification settings and Telegram integration.</summary>
public sealed class NyxIdNotificationsTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdNotificationsAction> ActionParser = new(
    [
        new("settings", NyxIdNotificationsAction.Settings, new(false, true, false)),
        new("update", NyxIdNotificationsAction.Update, new(true, false, false)),
        new("telegram_link", NyxIdNotificationsAction.TelegramLink, new(true, false, false)),
        new("telegram_disconnect", NyxIdNotificationsAction.TelegramDisconnect, new(true, false, true)),
    ], "settings");

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdNotificationsTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_notifications";

    public string Description =>
        "Manage notification settings and Telegram integration. " +
        "Actions: settings, update, telegram_link, telegram_disconnect.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
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
            return NyxIdClosedActionParser<NyxIdNotificationsAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        return parsed.Action switch
        {
            NyxIdNotificationsAction.Update => await UpdateAsync(token, args, ct),
            NyxIdNotificationsAction.TelegramLink => await _client.TelegramLinkAsync(token, ct),
            NyxIdNotificationsAction.TelegramDisconnect => await _client.TelegramDisconnectAsync(token, ct),
            NyxIdNotificationsAction.Settings => await _client.GetNotificationSettingsAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdNotificationsAction>.InvalidActionJson,
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

internal enum NyxIdNotificationsAction
{
    Settings,
    Update,
    TelegramLink,
    TelegramDisconnect,
}
