using System.Collections.ObjectModel;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    private const int LarkDurableReplyMaxTextChars = 2800;
    private const int LarkDurableReplyChunkBodyChars = 2700;

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

        if (TryComposeReplyText(platform, conversation, content, out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        return await SendRelayReplyAsync(
                platform,
                delivery.ReplyMessageId,
                replyText,
                replyToken,
                ct)
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

        if (TryComposeReplyText(platform, conversation, content, out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        if (!ShouldChunkDurableReply(platform, replyText))
        {
            return await SendRelayReplyAsync(
                    platform,
                    delivery.ReplyMessageId,
                    replyText,
                    agentKey,
                    ct)
                .ConfigureAwait(false);
        }

        var chunks = SplitReplyText(replyText, LarkDurableReplyChunkBodyChars);
        var firstSentActivityId = string.Empty;
        var firstPlatformMessageId = string.Empty;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkResult = await SendRelayReplyAsync(
                    platform,
                    delivery.ReplyMessageId,
                    AddChunkHeader(chunks[index], index + 1, chunks.Count),
                    agentKey,
                    ct)
                .ConfigureAwait(false);
            if (!chunkResult.Success)
            {
                _logger.LogWarning(
                    "Nyx relay durable chunked reply failed: platform={Platform}, messageId={MessageId}, chunk={Chunk}, chunks={Chunks}, error={ErrorCode}, detail={Detail}",
                    platform,
                    delivery.ReplyMessageId,
                    index + 1,
                    chunks.Count,
                    chunkResult.ErrorCode,
                    chunkResult.ErrorMessage);
                return chunkResult;
            }

            if (string.IsNullOrWhiteSpace(firstSentActivityId))
                firstSentActivityId = chunkResult.SentActivityId;
            if (string.IsNullOrWhiteSpace(firstPlatformMessageId))
                firstPlatformMessageId = chunkResult.PlatformMessageId;
        }

        return EmitResult.Sent(
            $"{firstSentActivityId}:chunks:{chunks.Count}",
            platformMessageId: firstPlatformMessageId);
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

        if (TryComposeReplyText(platform, conversation, content, out var replyText) is { } composeFailure)
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

    private async Task<EmitResult> SendRelayReplyAsync(
        string platform,
        string replyMessageId,
        string replyText,
        string credential,
        CancellationToken ct)
    {
        var result = await _nyxClient.SendChannelRelayTextReplyAsync(
                credential,
                replyMessageId,
                replyText,
                ct)
            .ConfigureAwait(false);
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

        if (_composers.TryGetValue(normalizedPlatform, out var composer))
        {
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

    private static string NormalizePlatformKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static bool ShouldChunkDurableReply(string platform, string replyText) =>
        IsLarkPlatform(platform) && replyText.Length > LarkDurableReplyMaxTextChars;

    private static bool IsLarkPlatform(string platform)
    {
        var normalized = NormalizePlatformKey(platform);
        return normalized is "lark" or "feishu";
    }

    private static string AddChunkHeader(string chunk, int number, int total) =>
        $"[{number}/{total}]\n{chunk}";

    private static List<string> SplitReplyText(string text, int maxChars)
    {
        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var remaining = text.Length - index;
            if (remaining <= maxChars)
            {
                chunks.Add(text[index..]);
                break;
            }

            var length = FindChunkLength(text, index, maxChars);
            chunks.Add(text.Substring(index, length).TrimEnd());
            index += length;
            while (index < text.Length && text[index] == '\n')
                index++;
        }

        return chunks;
    }

    private static int FindChunkLength(string text, int start, int maxChars)
    {
        var limit = Math.Min(text.Length, start + maxChars);
        var newline = text.LastIndexOf('\n', limit - 1, limit - start);
        if (newline > start)
            return AdjustSurrogateBoundary(text, start, newline - start);

        return AdjustSurrogateBoundary(text, start, maxChars);
    }

    private static int AdjustSurrogateBoundary(string text, int start, int length)
    {
        var end = start + length;
        if (end < text.Length &&
            end > start &&
            char.IsHighSurrogate(text[end - 1]) &&
            char.IsLowSurrogate(text[end]))
        {
            return length - 1;
        }

        return length;
    }
}
