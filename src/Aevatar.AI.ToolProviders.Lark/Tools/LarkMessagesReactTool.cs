using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesReactTool : AgentToolBase<LarkMessagesReactTool.Parameters>
{
    private const string DefaultEmojiType = "OK";
    private const string ChannelMessageIdKey = "channel.message_id";
    private const string ChannelPlatformMessageIdKey = "channel.platform_message_id";

    private static readonly Dictionary<string, string> EmojiAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = "OK",
        ["okay"] = "OK",
        ["了解"] = "OK",
        ["收到"] = "OK",
        ["thumbsup"] = "THUMBSUP",
        ["thumbs_up"] = "THUMBSUP",
        ["赞"] = "THUMBSUP",
        ["点赞"] = "THUMBSUP",
        ["done"] = "DONE",
        ["完成"] = "DONE",
        ["smile"] = "SMILE",
        ["微笑"] = "SMILE",
    };

    private readonly ILarkNyxClient _client;

    public LarkMessagesReactTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_react";

    public override string Description =>
        "Add an emoji reaction to a Lark message. " +
        "On a channel relay turn you can omit message_id to react to the current inbound message. " +
        "Defaults to emoji_type=OK (an acknowledgement like '了解').";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageId = ResolveMessageId(parameters.MessageId, out var usedCurrentMessage, out var messageError);
        if (!string.IsNullOrWhiteSpace(messageError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = messageError,
            });
        }

        var emojiType = NormalizeEmojiType(parameters.EmojiType);
        var response = await _client.CreateMessageReactionAsync(
            token,
            new LarkMessageReactionRequest(messageId!, emojiType),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                message_id = messageId,
                emoji_type = emojiType,
            });
        }

        var result = LarkProxyResponseParser.ParseReactionCreateSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            message_id = messageId,
            emoji_type = result.EmojiType ?? emojiType,
            reaction_id = result.ReactionId,
            operator_id = result.OperatorId,
            operator_type = result.OperatorType,
            action_time = result.ActionTime,
            used_current_message = usedCurrentMessage ? (bool?)true : null,
        });
    }

    private static string? ResolveMessageId(string? messageId, out bool usedCurrentMessage, out string? error)
    {
        usedCurrentMessage = false;
        error = null;

        if (TryValidateMessageId(messageId, out var explicitMessageId, out var explicitError))
        {
            return explicitMessageId;
        }

        if (!string.IsNullOrWhiteSpace(explicitError))
        {
            error = explicitError;
            return null;
        }

        var currentMessageId = AgentToolRequestContext.TryGet(ChannelPlatformMessageIdKey) ??
                               AgentToolRequestContext.TryGet(ChannelMessageIdKey);
        if (TryValidateMessageId(currentMessageId, out var current, out _))
        {
            usedCurrentMessage = true;
            return current;
        }

        if (!string.IsNullOrWhiteSpace(currentMessageId))
        {
            error = "Current turn metadata did not expose a Lark platform message_id (expected om_xxx). Pass message_id explicitly.";
            return null;
        }

        error = "message_id is required when current turn metadata does not include a Lark message id";
        return null;
    }

    private static bool TryValidateMessageId(string? raw, out string? normalized, out string? error)
    {
        normalized = raw?.Trim();
        error = null;
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!normalized.StartsWith("om_", StringComparison.OrdinalIgnoreCase))
        {
            error = "message_id must be a Lark message id like om_xxx";
            normalized = null;
            return false;
        }

        return true;
    }

    private static string NormalizeEmojiType(string? emojiType)
    {
        var trimmed = emojiType?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultEmojiType;

        return EmojiAliases.TryGetValue(trimmed, out var alias)
            ? alias
            : trimmed;
    }

    public sealed class Parameters
    {
        public string? MessageId { get; set; }
        public string? EmojiType { get; set; }
    }
}
