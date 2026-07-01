using System.Collections.ObjectModel;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    internal const int LarkReplyTextChunkLimit = 1_800;
    internal const int LarkReplyMaxChunks = 32;
    private static readonly int LarkReplyChunkHeaderReserve = FormatChunkHeader(
        LarkReplyMaxChunks,
        LarkReplyMaxChunks).Length;

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

        if (TryComposeReplyText(
                platform,
                conversation,
                content,
                out var normalizedPlatform,
                out var replyText) is { } composeFailure)
        {
            return composeFailure;
        }

        var result = await SendRelayTextReplyAsync(
                normalizedPlatform,
                replyToken,
                delivery.ReplyMessageId,
                replyText,
                ct)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        return result;
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

        return await SendAsync(
                platform,
                conversation,
                content,
                delivery,
                agentKey,
                ct)
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

        if (TryComposeReplyText(
                platform,
                conversation,
                content,
                out _,
                out var replyText) is { } composeFailure)
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

    private async Task<EmitResult> SendRelayTextReplyAsync(
        string normalizedPlatform,
        string replyToken,
        string replyMessageId,
        string replyText,
        CancellationToken ct)
    {
        var chunkPayloadLimit = LarkReplyTextChunkLimit - LarkReplyChunkHeaderReserve;
        var chunks = ShouldChunkRelayText(normalizedPlatform, replyText)
            ? SplitReplyText(replyText, chunkPayloadLimit).ToArray()
            : [replyText];

        if (chunks.Length > LarkReplyMaxChunks)
        {
            return EmitResult.Failed(
                "relay_reply_too_large",
                $"Relay reply would require {chunks.Length} Lark messages, above the supported bound of {LarkReplyMaxChunks}.");
        }

        string? firstMessageId = null;
        string? lastPlatformMessageId = null;
        for (var index = 0; index < chunks.Length; index++)
        {
            var chunkText = chunks.Length == 1
                ? chunks[index]
                : $"{FormatChunkHeader(index + 1, chunks.Length)}{chunks[index]}";
            var result = await _nyxClient.SendChannelRelayTextReplyAsync(
                    replyToken,
                    replyMessageId,
                    chunkText,
                    ct)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Nyx relay reply delivery failed: platform={Platform}, messageId={MessageId}, chunk={ChunkIndex}/{ChunkCount}, detail={Detail}",
                    normalizedPlatform,
                    replyMessageId,
                    index + 1,
                    chunks.Length,
                    result.Detail);
                return EmitResult.Failed(
                    "relay_reply_rejected",
                    chunks.Length == 1
                        ? result.Detail ?? "Nyx relay reply rejected."
                        : $"Nyx relay reply chunk {index + 1}/{chunks.Length} rejected: {result.Detail ?? "unknown error"}");
            }

            firstMessageId ??= result.MessageId;
            lastPlatformMessageId = result.PlatformMessageId ?? lastPlatformMessageId;
        }

        return EmitResult.Sent(
            firstMessageId ?? $"nyx-relay:{replyMessageId}",
            platformMessageId: lastPlatformMessageId);
    }

    private EmitResult? TryComposeReplyText(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        out string normalizedPlatform,
        out string replyText)
    {
        normalizedPlatform = string.Empty;
        replyText = string.Empty;
        normalizedPlatform = NormalizePlatformKey(platform);
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

    private static bool ShouldChunkRelayText(string normalizedPlatform, string text) =>
        IsLarkPlatform(normalizedPlatform) && text.Length > LarkReplyTextChunkLimit;

    private static bool IsLarkPlatform(string normalizedPlatform) =>
        string.Equals(normalizedPlatform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(normalizedPlatform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string FormatChunkHeader(int chunkIndex, int chunkCount) =>
        $"[{chunkIndex}/{chunkCount}]\n";

    internal static IEnumerable<string> SplitReplyText(string text, int chunkLimit)
    {
        if (chunkLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkLimit), "Chunk limit must be positive.");
        if (string.IsNullOrEmpty(text))
            yield break;

        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= chunkLimit)
            {
                yield return text[offset..];
                yield break;
            }

            var length = FindChunkLength(text, offset, chunkLimit);
            yield return text.Substring(offset, length).TrimEnd();
            offset += length;
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
                offset++;
        }
    }

    private static int FindChunkLength(string text, int offset, int chunkLimit)
    {
        var hardEnd = Math.Min(text.Length, offset + chunkLimit);
        for (var index = hardEnd - 1; index > offset; index--)
        {
            if (text[index] == '\n')
                return index - offset + 1;
        }

        for (var index = hardEnd - 1; index > offset; index--)
        {
            if (char.IsWhiteSpace(text[index]))
                return index - offset;
        }

        if (chunkLimit > 1 && char.IsHighSurrogate(text[hardEnd - 1]) && hardEnd < text.Length)
            return chunkLimit - 1;

        return chunkLimit;
    }

    private static string NormalizePlatformKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
