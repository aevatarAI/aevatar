using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesSendTool : AgentToolBase<LarkMessagesSendTool.Parameters>
{
    private static readonly HashSet<string> AllowedTargetTypes =
    [
        "chat_id",
        "open_id",
        "user_id",
    ];

    private readonly ILarkNyxClient _client;

    public LarkMessagesSendTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_send";

    public override string Description =>
        "Proactively send a Lark message through Nyx-backed transport. " +
        "Use this for notifications or workflow side effects, not for replying to the current inbound relay turn.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var targetType = (parameters.TargetType ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedTargetTypes.Contains(targetType))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "target_type must be one of: chat_id, open_id, user_id",
            });
        }

        var targetId = parameters.TargetId?.Trim();
        if (string.IsNullOrWhiteSpace(targetId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "target_id is required" });

        var warnings = new List<string>();
        var normalizedMessageType = NormalizeMessageType(parameters.MessageType);
        if (normalizedMessageType is null)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "message_type must be one of: text, interactive_card",
            });
        }

        string contentJson;
        switch (normalizedMessageType)
        {
            case "text":
                if (string.IsNullOrWhiteSpace(parameters.Text))
                    return LarkProxyResponseParser.Serialize(new { success = false, error = "text is required when message_type=text" });
                contentJson = JsonSerializer.Serialize(new { text = parameters.Text });
                break;
            case "interactive":
                if (string.IsNullOrWhiteSpace(parameters.CardJson))
                    return LarkProxyResponseParser.Serialize(new { success = false, error = "card_json is required when message_type=interactive_card" });
                try
                {
                    using var _ = JsonDocument.Parse(parameters.CardJson);
                }
                catch (JsonException ex)
                {
                    return LarkProxyResponseParser.Serialize(new { success = false, error = $"card_json is not valid JSON: {ex.Message}" });
                }

                contentJson = parameters.CardJson;
                break;
            default:
                throw new InvalidOperationException($"Unsupported normalized message type: {normalizedMessageType}");
        }

        if (!string.IsNullOrWhiteSpace(parameters.ThreadId))
            warnings.Add("thread_id is ignored for proactive send because this tool uses the message-send path, not reply-in-thread.");

        var response = await _client.SendMessageAsync(
            token,
            new LarkSendMessageRequest(
                TargetType: targetType,
                TargetId: targetId,
                MessageType: normalizedMessageType,
                ContentJson: contentJson,
                IdempotencyKey: parameters.IdempotencyKey?.Trim()),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                target_type = targetType,
                target_id = targetId,
                warnings = warnings.Count == 0 ? null : warnings,
            });
        }

        var result = LarkProxyResponseParser.ParseSendSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            target_type = targetType,
            target_id = targetId,
            message_id = result.MessageId,
            chat_id = result.ChatId,
            create_time = result.CreateTime,
            warnings = warnings.Count == 0 ? null : warnings,
        });
    }

    private static string? NormalizeMessageType(string? messageType) =>
        (messageType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "text" => "text",
            "interactive_card" or "interactive" => "interactive",
            _ => null,
        };

    public sealed class Parameters
    {
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public string? MessageType { get; set; }
        public string? Text { get; set; }
        public string? CardJson { get; set; }
        public string? IdempotencyKey { get; set; }
        public string? ThreadId { get; set; }
    }
}
