using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesReactionsDeleteTool : AgentToolBase<LarkMessagesReactionsDeleteTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkMessagesReactionsDeleteTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_reactions_delete";

    public override string Description =>
        "Delete a specific Lark message reaction record by reaction_id. " +
        "On a channel relay turn you can omit message_id to operate on the current inbound message.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageId = LarkMessageIdResolver.ResolveOrCurrent(parameters.MessageId, out var usedCurrentMessage, out var messageError);
        if (!string.IsNullOrWhiteSpace(messageError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = messageError });

        var reactionId = parameters.ReactionId?.Trim();
        if (string.IsNullOrWhiteSpace(reactionId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "reaction_id is required." });

        var response = await _client.DeleteMessageReactionAsync(
            token,
            new LarkMessageReactionDeleteRequest(messageId!, reactionId),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                message_id = messageId,
                reaction_id = reactionId,
            });
        }

        var result = LarkProxyResponseParser.ParseReactionCreateSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            message_id = messageId,
            reaction_id = result.ReactionId ?? reactionId,
            emoji_type = result.EmojiType,
            operator_id = result.OperatorId,
            operator_type = result.OperatorType,
            action_time = result.ActionTime,
            used_current_message = usedCurrentMessage ? (bool?)true : null,
        });
    }

    public sealed class Parameters
    {
        public string? MessageId { get; set; }
        public string? ReactionId { get; set; }
    }
}
