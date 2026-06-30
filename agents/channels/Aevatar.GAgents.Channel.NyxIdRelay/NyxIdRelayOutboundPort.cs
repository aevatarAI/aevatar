using System.Collections.ObjectModel;
using System.Globalization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    private const string LarkPlatformKey = "lark";
    private const int LarkRelayTextChunkLimit = 28_000;

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
        string replyToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(delivery);

        if (string.IsNullOrWhiteSpace(replyToken))
        {
            return EmitResult.Failed(
                "reply_token_missing_or_expired",
                "Relay reply is missing the access token required for channel-relay/reply.");
        }

        if (string.IsNullOrWhiteSpace(delivery.ReplyMessageId))
        {
            return EmitResult.Failed(
                "missing_reply_message_id",
                "Relay reply is missing the source message id required for channel-relay/reply.");
        }

        if (TryComposeReplyText(platform, conversation, content, preserveLongText: false, out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        return await SendSingleTextReplyAsync(platform, delivery.ReplyMessageId, replyText, replyToken, ct)
            .ConfigureAwait(false);
    }

    public async Task<EmitResult> SendWithAgentKeyAsync(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        OutboundDeliveryContext delivery,
        string agentKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentKey))
        {
            return EmitResult.Failed(
                "bot_agent_key_missing",
                "Relay reply is missing the bot agent key required for channel-relay/reply.");
        }

        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(delivery);

        if (string.IsNullOrWhiteSpace(delivery.ReplyMessageId))
        {
            return EmitResult.Failed(
                "missing_reply_message_id",
                "Relay reply is missing the source message id required for channel-relay/reply.");
        }

        if (TryComposeReplyText(platform, conversation, content, preserveLongText: true, out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        if (!ShouldChunkDurableReply(platform, replyText))
        {
            return await SendSingleTextReplyAsync(platform, delivery.ReplyMessageId, replyText, agentKey, ct)
                .ConfigureAwait(false);
        }

        return await SendChunkedDurableTextReplyAsync(platform, delivery.ReplyMessageId, replyText, agentKey, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Edits a previously-sent relay reply in place. Used by the progressive streaming reply path
    /// to drive edit-in-place updates while the LLM is still generating.
    /// </summary>
    /// <param name="platformMessageId">
    /// The upstream platform message identifier returned by a prior <see cref="SendAsync"/> call
    /// (Lark: <c>om_xxx</c>).
    /// </param>
    /// <param name="replyToken">
    /// Actor-owned relay reply token resolved from <c>ConversationTurnRuntimeContext.NyxRelayReplyToken</c>.
    /// </param>
    public async Task<EmitResult> UpdateAsync(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        OutboundDeliveryContext delivery,
        string platformMessageId,
        string replyToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(delivery);

        if (string.IsNullOrWhiteSpace(replyToken))
        {
            return EmitResult.Failed(
                "reply_token_missing_or_expired",
                "Relay reply update is missing the access token required for channel-relay/reply/update.");
        }

        if (string.IsNullOrWhiteSpace(platformMessageId))
        {
            return EmitResult.Failed(
                "missing_platform_message_id",
                "Relay reply update requires the upstream platform message id captured from the initial send.");
        }

        if (TryComposeReplyText(platform, conversation, content, preserveLongText: false, out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        var result = await _nyxClient.UpdateChannelRelayTextReplyAsync(
            replyToken,
            platformMessageId,
            replyText,
            ct);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Nyx relay reply update failed: platform={Platform}, platformMessageId={PlatformMessageId}, detail={Detail}, editUnsupported={EditUnsupported}, failureKind={FailureKind}, httpStatus={HttpStatus}, rawErrorKey={RawErrorKey}, rawErrorCode={RawErrorCode}",
                platform,
                platformMessageId,
                result.Detail,
                result.EditUnsupported,
                result.FailureKind,
                result.HttpStatus,
                result.RawErrorKey,
                result.RawErrorCode);
            var errorCode = result.EditUnsupported
                ? "relay_reply_edit_unsupported"
                : "relay_reply_update_rejected";
            return EmitResult.Failed(
                errorCode,
                result.Detail ?? "Nyx relay reply update rejected.",
                result.RetryAfter,
                ComposeCapability.Unsupported,
                result.FailureKind,
                result.HttpStatus,
                result.RawErrorKey,
                result.RawErrorCode);
        }

        return EmitResult.Sent(
            $"nyx-relay-update:{platformMessageId}",
            platformMessageId: result.PlatformMessageId ?? platformMessageId);
    }

    private EmitResult? TryComposeReplyText(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        bool preserveLongText,
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

        if (_composers.TryGetValue(normalizedPlatform, out var composer))
        {
            var composeContext = new ComposeContext
            {
                Conversation = conversation.Clone(),
            };
            if (preserveLongText && normalizedPlatform == LarkPlatformKey)
            {
                composeContext.Capabilities = new ChannelCapabilities
                {
                    MaxMessageLength = 0,
                };
            }

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
        }
        else
        {
            replyText = NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(content);
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            return EmitResult.Failed(
                "empty_reply",
                "Relay outbound could not render a non-empty reply payload.");
        }

        return null;
    }

    private async Task<EmitResult> SendSingleTextReplyAsync(
        string platform,
        string replyMessageId,
        string replyText,
        string bearerToken,
        CancellationToken ct)
    {
        var result = await _nyxClient.SendChannelRelayTextReplyAsync(
            bearerToken,
            replyMessageId,
            replyText,
            ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "Nyx relay reply delivery failed: platform={Platform}, messageId={MessageId}, detail={Detail}",
                platform,
                replyMessageId,
                result.Detail);
            return EmitResult.Failed(
                "relay_reply_rejected",
                result.Detail ?? "Nyx relay reply rejected.");
        }

        return EmitResult.Sent(
            result.MessageId ?? $"nyx-relay:{replyMessageId}",
            platformMessageId: result.PlatformMessageId);
    }

    private async Task<EmitResult> SendChunkedDurableTextReplyAsync(
        string platform,
        string replyMessageId,
        string replyText,
        string bearerToken,
        CancellationToken ct)
    {
        var chunks = SplitLarkRelayText(replyText).ToArray();
        string? lastMessageId = null;
        string? lastPlatformMessageId = null;
        for (var i = 0; i < chunks.Length; i++)
        {
            var result = await _nyxClient.SendChannelRelayTextReplyAsync(
                bearerToken,
                replyMessageId,
                chunks[i],
                ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Nyx relay chunked reply delivery failed: platform={Platform}, messageId={MessageId}, chunk={ChunkIndex}, chunks={ChunkCount}, detail={Detail}",
                    platform,
                    replyMessageId,
                    i + 1,
                    chunks.Length,
                    result.Detail);
                return EmitResult.Failed(
                    "relay_reply_chunk_rejected",
                    result.Detail ?? $"Nyx relay reply chunk {i + 1}/{chunks.Length} rejected.");
            }

            lastMessageId = result.MessageId;
            lastPlatformMessageId = result.PlatformMessageId;
        }

        return EmitResult.Sent(
            lastMessageId ?? $"nyx-relay:{replyMessageId}:chunks:{chunks.Length}",
            ComposeCapability.Exact,
            lastPlatformMessageId);
    }

    private static bool ShouldChunkDurableReply(string platform, string replyText) =>
        NormalizePlatformKey(platform) == LarkPlatformKey &&
        CountTextElements(replyText) > LarkRelayTextChunkLimit;

    private static IEnumerable<string> SplitLarkRelayText(string replyText)
    {
        var totalParts = Math.Max(1, (int)Math.Ceiling((double)CountTextElements(replyText) / LarkRelayTextChunkLimit));
        while (true)
        {
            var parts = SplitTextWithPartHeaders(replyText, totalParts).ToArray();
            if (parts.Length == totalParts)
                return parts;

            totalParts = parts.Length;
        }
    }

    private static IEnumerable<string> SplitTextWithPartHeaders(string text, int totalParts)
    {
        var offset = 0;
        for (var partNumber = 1; offset < text.Length; partNumber++)
        {
            var header = $"({partNumber}/{totalParts})\n";
            var payloadLimit = LarkRelayTextChunkLimit - CountTextElements(header);
            if (payloadLimit <= 0)
                throw new InvalidOperationException("Lark relay text chunk header exceeds the chunk limit.");

            var payload = TakeTextElements(text, offset, payloadLimit, out var nextOffset);
            yield return header + payload;
            offset = nextOffset;
        }
    }

    private static string TakeTextElements(string text, int offset, int maxTextElements, out int nextOffset)
    {
        var remaining = text.AsSpan(offset);
        var indexes = StringInfo.ParseCombiningCharacters(remaining.ToString());
        if (indexes.Length <= maxTextElements)
        {
            nextOffset = text.Length;
            return text[offset..];
        }

        var length = indexes[maxTextElements];
        nextOffset = offset + length;
        return text.Substring(offset, length);
    }

    private static int CountTextElements(string value) =>
        new StringInfo(value).LengthInTextElements;

    private static string NormalizePlatformKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
