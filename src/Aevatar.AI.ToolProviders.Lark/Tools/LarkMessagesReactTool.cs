using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesReactTool : AgentToolBase<LarkMessagesReactTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkMessagesReactTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_react";

    public override string Description =>
        "Add an emoji reaction to a Lark message. " +
        "Requires an explicit external message_id; current relay replies must be sent as the final answer. " +
        "Defaults to emoji_type=OK (an acknowledgement like '了解').";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    // Refactor (issue1378/first-slice):
    //   Old pattern: ResolveOrCurrent used current message when reaction message_id was missing.
    //   New principle: missing reaction message_id returns structured error; external tools path remains.
    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.CredentialRef;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageId = LarkMessageIdResolver.ResolveExplicit(parameters.MessageId, out var messageError);
        if (!string.IsNullOrWhiteSpace(messageError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                code = "missing_message_id",
                error = messageError,
                recommended_action = "final_answer",
            });
        }

        var emojiType = LarkReactionEmojiHelper.NormalizeOrDefault(parameters.EmojiType);
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
        });
    }

    public sealed class Parameters
    {
        public string? MessageId { get; set; }
        public string? EmojiType { get; set; }
    }
}
