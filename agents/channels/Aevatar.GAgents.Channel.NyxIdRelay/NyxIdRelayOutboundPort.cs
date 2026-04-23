using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    private readonly NyxIdApiClient _nyxClient;
    private readonly LarkMessageComposer? _larkComposer;
    private readonly ILogger<NyxIdRelayOutboundPort> _logger;

    public NyxIdRelayOutboundPort(
        NyxIdApiClient nyxClient,
        ILogger<NyxIdRelayOutboundPort> logger,
        LarkMessageComposer? larkComposer = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _larkComposer = larkComposer;
    }

    public async Task<EmitResult> SendAsync(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        OutboundDeliveryContext delivery,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(delivery);

        if (string.IsNullOrWhiteSpace(delivery.ReplyAccessToken))
        {
            return EmitResult.Failed(
                "missing_reply_access_token",
                "Relay reply is missing the access token required for channel-relay/reply.");
        }

        if (string.IsNullOrWhiteSpace(delivery.ReplyMessageId))
        {
            return EmitResult.Failed(
                "missing_reply_message_id",
                "Relay reply is missing the source message id required for channel-relay/reply.");
        }

        var replyText = ComposeReplyText(platform, conversation, content);
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return EmitResult.Failed(
                "empty_reply",
                "Relay outbound could not render a non-empty reply payload.");
        }

        var result = await _nyxClient.SendChannelRelayTextReplyAsync(
            delivery.ReplyAccessToken,
            delivery.ReplyMessageId,
            replyText,
            ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Nyx relay reply delivery failed: platform={Platform}, messageId={MessageId}, detail={Detail}",
                platform,
                delivery.ReplyMessageId,
                result.Detail);
            return EmitResult.Failed(
                "relay_reply_rejected",
                result.Detail ?? "Nyx relay reply rejected.");
        }

        return EmitResult.Sent(result.MessageId ?? $"nyx-relay:{delivery.ReplyMessageId}");
    }

    private string ComposeReplyText(string platform, ConversationReference conversation, MessageContent content)
    {
        var normalizedPlatform = string.IsNullOrWhiteSpace(platform)
            ? string.Empty
            : platform.Trim().ToLowerInvariant();

        if (normalizedPlatform == "lark" && _larkComposer is not null)
        {
            var composed = _larkComposer.Compose(
                content,
                new ComposeContext
                {
                    Conversation = conversation.Clone(),
                    Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
                });
            return composed.PlainText ?? string.Empty;
        }

        return content.Text ?? string.Empty;
    }
}
