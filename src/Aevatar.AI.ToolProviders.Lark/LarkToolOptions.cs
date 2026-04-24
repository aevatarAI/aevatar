namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkToolOptions
{
    public string ProviderSlug { get; set; } = "api-lark-bot";
    public bool EnableMessageSend { get; set; } = true;
    public bool EnableMessageReply { get; set; } = true;
    public bool EnableMessageReactionCreate { get; set; } = true;
    public bool EnableMessageReactionList { get; set; } = true;
    public bool EnableMessageReactionDelete { get; set; } = true;
    public bool EnableMessageSearch { get; set; } = true;
    public bool EnableMessageBatchGet { get; set; } = true;
    public bool EnableChatLookup { get; set; } = true;
    public bool EnableSheetsAppendRows { get; set; } = true;
    public bool EnableApprovalsList { get; set; } = true;
    public bool EnableApprovalsAct { get; set; } = true;
}
