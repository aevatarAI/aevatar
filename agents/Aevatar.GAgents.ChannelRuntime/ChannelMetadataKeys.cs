namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Typed metadata keys for channel runtime context.
/// Used in ChatRequestEvent.Metadata to pass channel-specific context to downstream actors.
/// </summary>
public static class ChannelMetadataKeys
{
    public const string Platform = "channel.platform";
    public const string SenderId = "channel.sender_id";
    public const string SenderName = "channel.sender_name";
    public const string ConversationId = "channel.conversation_id";
    public const string MessageId = "channel.message_id";
    public const string PlatformMessageId = "channel.platform_message_id";
    public const string ChatType = "channel.chat_type";
}
