using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Describes the inbound activity the test driver wants the fixture to synthesize.
/// </summary>
/// <remarks>
/// The seed is channel-agnostic. Concrete fixtures translate it into the native webhook body or gateway event expected by
/// the adapter under test.
/// </remarks>
public sealed record InboundActivitySeed(
    ActivityType ActivityType,
    ConversationScope Scope,
    string ConversationKey,
    string SenderCanonicalId,
    string SenderDisplayName,
    string Text,
    IReadOnlyList<ParticipantRef>? Mentions = null,
    string? ReplyToActivityId = null,
    string? PlatformMessageId = null)
{
    /// <summary>
    /// Creates one minimal direct-message seed with only required fields.
    /// </summary>
    public static InboundActivitySeed DirectMessage(string text) => new(
        ActivityType.Message,
        ConversationScope.DirectMessage,
        ConversationKey: "dm-1",
        SenderCanonicalId: "user-1",
        SenderDisplayName: "Test User",
        Text: text);

    /// <summary>
    /// Creates one minimal group-chat seed that must map to a distinct canonical conversation key.
    /// </summary>
    public static InboundActivitySeed GroupMessage(string conversationKey, string text) => new(
        ActivityType.Message,
        ConversationScope.Group,
        ConversationKey: conversationKey,
        SenderCanonicalId: "user-1",
        SenderDisplayName: "Test User",
        Text: text);
}
