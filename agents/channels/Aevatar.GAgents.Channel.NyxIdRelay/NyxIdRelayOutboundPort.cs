using System.Collections.ObjectModel;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    private readonly NyxIdApiClient _nyxClient;
    private readonly IReadOnlyDictionary<string, IMessageComposer> _composers;
    private readonly ILogger<NyxIdRelayOutboundPort> _logger;

    public NyxIdRelayOutboundPort(
        NyxIdApiClient nyxClient,
        ILogger<NyxIdRelayOutboundPort> logger,
        IEnumerable<IMessageComposer> composers)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(composers);

        var composerLookup = new Dictionary<string, IMessageComposer>(StringComparer.OrdinalIgnoreCase);
        foreach (var composer in composers)
        {
            ArgumentNullException.ThrowIfNull(composer);

            var platformKey = NormalizePlatformKey(composer.Channel.Value);
            if (!composerLookup.TryAdd(platformKey, composer))
            {
                throw new InvalidOperationException(
                    $"Multiple message composers are registered for platform '{platformKey}'.");
            }
        }

        _composers = new ReadOnlyDictionary<string, IMessageComposer>(composerLookup);
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

        if (TryComposeReplyText(platform, conversation, content, out var replyText) is { } composeFailure)
        {
            return composeFailure;
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

    private EmitResult? TryComposeReplyText(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        out string replyText)
    {
        replyText = string.Empty;
        var normalizedPlatform = NormalizePlatformKey(platform);
        if (string.IsNullOrWhiteSpace(normalizedPlatform))
        {
            return EmitResult.Failed(
                "platform_required",
                "Relay outbound is missing the platform required to resolve a message composer.");
        }

        if (!_composers.TryGetValue(normalizedPlatform, out var composer))
        {
            return EmitResult.Failed(
                "composer_not_found",
                $"Relay outbound has no message composer registered for platform '{normalizedPlatform}'.");
        }

        var composeContext = new ComposeContext
        {
            Conversation = conversation.Clone(),
        };
        if (composer.Evaluate(content, composeContext) == ComposeCapability.Unsupported)
        {
            return EmitResult.Failed(
                "composer_unsupported",
                $"Relay outbound composer for platform '{normalizedPlatform}' cannot express the requested message content.");
        }

        if (composer.Compose(content, composeContext) is not IPlainTextComposedMessage plainTextPayload)
        {
            return EmitResult.Failed(
                "plain_text_payload_unavailable",
                $"Relay outbound composer for platform '{normalizedPlatform}' does not expose a plain-text payload.");
        }

        replyText = plainTextPayload.PlainText;
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return EmitResult.Failed(
                "empty_reply",
                "Relay outbound could not render a non-empty reply payload.");
        }

        return null;
    }

    private static string NormalizePlatformKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
