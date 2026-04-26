using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesBatchGetTool : AgentToolBase<LarkMessagesBatchGetTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkMessagesBatchGetTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_batch_get";

    public override string Description =>
        "Batch fetch full Lark message details by message IDs. " +
        "Use this to inspect known messages after search or when the user gives you om_xxx IDs.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var messageIds = parameters.MessageIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (messageIds is not { Length: > 0 })
            return LarkProxyResponseParser.Serialize(new { success = false, error = "message_ids must contain at least one Lark message id." });
        if (messageIds.Length > 50)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "message_ids exceeds the maximum of 50 values." });

        foreach (var messageId in messageIds)
        {
            if (!LarkMessageIdResolver.TryValidate(messageId, out _, out var validationError))
                return LarkProxyResponseParser.Serialize(new { success = false, error = validationError });
        }

        var response = await _client.BatchGetMessagesAsync(
            token,
            new LarkMessagesBatchGetRequest(messageIds),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        var result = LarkProxyResponseParser.ParseMessageBatchGetSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            total = result.Messages.Count,
            messages = result.Messages.Select(message => new
            {
                message_id = message.MessageId,
                msg_type = message.MessageType,
                content = message.Content,
                content_json = message.ContentJson,
                chat_id = message.ChatId,
                create_time = message.CreateTime,
                thread_id = message.ThreadId,
                reply_to = message.ReplyTo,
                deleted = message.Deleted,
                updated = message.Updated,
                sender = new
                {
                    id = message.SenderId,
                    name = message.SenderName,
                    sender_type = message.SenderType,
                },
                mentions = message.Mentions.Select(mention => new
                {
                    id = mention.Id,
                    key = mention.Key,
                    name = mention.Name,
                }).ToArray(),
            }).ToArray(),
        });
    }

    public sealed class Parameters
    {
        public List<string>? MessageIds { get; set; }
    }
}
