using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>External protocol conversion for rich text and attachments, without identity or authentication authority.</summary>
public interface INyxIdRelayContentAdapter : IChannelPlatformBehavior
{
    /// <summary>Returns true only for a positively recognized typed or raw message shape.</summary>
    bool SupportsContent(NyxIdRelayCallbackPayload payload);

    MessageContent NormalizeContent(NyxIdRelayCallbackPayload payload);
}

/// <summary>External protocol conversion for platform conversation and delivery addresses.</summary>
public interface INyxIdRelayConversationAdapter : IChannelPlatformBehavior
{
    NyxIdRelayConversationNormalization NormalizeConversation(NyxIdRelayCallbackPayload payload,
        ConversationScope mappedScope, string conversationIdentity, bool isInteraction);
}

public sealed record NyxIdRelayConversationNormalization(ConversationScope Scope, string ConversationIdentity,
    TransportExtras DeliveryFacts, string? ErrorCode = null);

/// <summary>External protocol conversion of mentions only.</summary>
public interface INyxIdRelayMentionAdapter : IChannelPlatformBehavior
{
    List<ParticipantRef> NormalizeMentions(NyxIdRelayCallbackPayload payload);
}

/// <summary>External protocol conversion of an interactive card submission only.</summary>
public interface INyxIdRelayInteractionAdapter : IChannelPlatformBehavior
{
    CardActionSubmission? NormalizeInteraction(NyxIdRelayCallbackPayload payload);
}
