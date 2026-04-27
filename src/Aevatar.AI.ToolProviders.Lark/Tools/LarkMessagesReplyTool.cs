using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesReplyTool : AgentToolBase<LarkMessagesReplyTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkMessagesReplyTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_reply";

    public override string Description =>
        "Reply to a specific Lark message through Nyx-backed transport. " +
        "On a channel relay turn you can omit message_id to reply to the current inbound Lark message. " +
        "Supports text replies and interactive cards.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageId = LarkMessageIdResolver.ResolveOrCurrent(parameters.MessageId, out var usedCurrentMessage, out var messageError);
        if (!string.IsNullOrWhiteSpace(messageError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = messageError });

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

        var response = await _client.ReplyToMessageAsync(
            token,
            new LarkReplyMessageRequest(
                MessageId: messageId!,
                MessageType: normalizedMessageType,
                ContentJson: contentJson,
                ReplyInThread: parameters.ReplyInThread ?? false,
                IdempotencyKey: parameters.IdempotencyKey?.Trim()),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                message_id = messageId,
                message_type = normalizedMessageType,
            });
        }

        var result = LarkProxyResponseParser.ParseSendSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            message_id = result.MessageId ?? messageId,
            chat_id = result.ChatId,
            create_time = result.CreateTime,
            reply_in_thread = parameters.ReplyInThread ?? false,
            used_current_message = usedCurrentMessage ? (bool?)true : null,
        });
    }

    private static string? NormalizeMessageType(string? messageType) =>
        (messageType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "text" => "text",
            "interactive_card" or "interactive" => "interactive",
            _ => null,
        };

    public sealed class Parameters
    {
        public string? MessageId { get; set; }
        public string? MessageType { get; set; }
        public string? Text { get; set; }
        public string? CardJson { get; set; }
        public bool? ReplyInThread { get; set; }
        public string? IdempotencyKey { get; set; }
    }
}
