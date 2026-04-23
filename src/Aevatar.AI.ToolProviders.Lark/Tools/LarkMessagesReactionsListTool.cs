using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesReactionsListTool : AgentToolBase<LarkMessagesReactionsListTool.Parameters>
{
    private static readonly HashSet<string> AllowedUserIdTypes =
    [
        "user_id",
        "union_id",
        "open_id",
    ];

    private readonly ILarkNyxClient _client;

    public LarkMessagesReactionsListTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_reactions_list";

    public override string Description =>
        "List emoji reaction records on a Lark message. " +
        "On a channel relay turn you can omit message_id to inspect reactions on the current inbound message.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageId = LarkMessageIdResolver.ResolveOrCurrent(parameters.MessageId, out var usedCurrentMessage, out var messageError);
        if (!string.IsNullOrWhiteSpace(messageError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = messageError });

        var userIdType = parameters.UserIdType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(userIdType) && !AllowedUserIdTypes.Contains(userIdType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "user_id_type must be one of: user_id, union_id, open_id" });

        var pageSize = parameters.PageSize is > 0 ? parameters.PageSize.Value : 20;
        if (pageSize is < 1 or > 100)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "page_size must be between 1 and 100." });

        var response = await _client.ListMessageReactionsAsync(
            token,
            new LarkMessageReactionListRequest(
                MessageId: messageId!,
                EmojiType: LarkReactionEmojiHelper.NormalizeOptional(parameters.EmojiType),
                PageSize: pageSize,
                PageToken: parameters.PageToken?.Trim(),
                UserIdType: userIdType),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error, message_id = messageId });

        var result = LarkProxyResponseParser.ParseReactionListSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            message_id = messageId,
            count = result.Items.Count,
            has_more = result.HasMore,
            page_token = result.PageToken,
            used_current_message = usedCurrentMessage ? (bool?)true : null,
            items = result.Items.Select(item => new
            {
                reaction_id = item.ReactionId,
                operator_id = item.OperatorId,
                operator_type = item.OperatorType,
                action_time = item.ActionTime,
                emoji_type = item.EmojiType,
            }).ToArray(),
        });
    }

    public sealed class Parameters
    {
        public string? MessageId { get; set; }
        public string? EmojiType { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
        public string? UserIdType { get; set; }
    }
}
