using System.Collections.ObjectModel;
using System.Globalization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    private const int MaxLarkTextLength = 30_000;
    private const int ChunkMarkerOverhead = 60;
    private const string ContinuesSuffixFormat = "\n\n[part {0}/{1} - continues]";
    private const string ContinuedPrefixFormat = "[part {0}/{1} - continued]\n\n";

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

        var normalizedPlatform = NormalizePlatformKey(platform);
        var chunks = SplitRelayReplyText(normalizedPlatform, replyText);
        var sentActivityIds = new List<string>(chunks.Count);
        var platformMessageIds = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var result = await _nyxClient.SendChannelRelayTextReplyAsync(
                replyToken,
                delivery.ReplyMessageId,
                chunks[i],
                ct);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Nyx relay reply delivery failed: platform={Platform}, messageId={MessageId}, chunk={Chunk}, totalChunks={TotalChunks}, detail={Detail}",
                    platform,
                    delivery.ReplyMessageId,
                    i + 1,
                    chunks.Count,
                    result.Detail);
                return EmitResult.Failed(
                    "relay_reply_rejected",
                    result.Detail ?? "Nyx relay reply rejected.");
            }

            sentActivityIds.Add(result.MessageId ?? $"nyx-relay:{delivery.ReplyMessageId}:{i + 1}");
            if (!string.IsNullOrWhiteSpace(result.PlatformMessageId))
                platformMessageIds.Add(result.PlatformMessageId);
        }

        return EmitResult.Sent(
            string.Join(",", sentActivityIds),
            platformMessageId: string.Join(",", platformMessageIds));
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

    private static IReadOnlyList<string> SplitRelayReplyText(string normalizedPlatform, string replyText)
    {
        if (!IsLarkPlatform(normalizedPlatform) ||
            new StringInfo(replyText).LengthInTextElements <= MaxLarkTextLength)
            return [replyText];

        var contentBudget = Math.Max(1, MaxLarkTextLength - ChunkMarkerOverhead);
        var rawChunks = SplitRaw(replyText, contentBudget);
        if (rawChunks.Count == 1)
            return rawChunks;

        var total = rawChunks.Count;
        var rendered = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            var partNumber = i + 1;
            var prefix = i > 0
                ? string.Format(ContinuedPrefixFormat, partNumber, total)
                : string.Empty;
            var suffix = i < total - 1
                ? string.Format(ContinuesSuffixFormat, partNumber, total)
                : string.Empty;
            rendered.Add(prefix + rawChunks[i] + suffix);
        }

        return rendered;
    }

    private static List<string> SplitRaw(string text, int contentBudget)
    {
        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text[offset..];
            if (new StringInfo(remaining).LengthInTextElements <= contentBudget)
            {
                chunks.Add(remaining);
                break;
            }

            var end = offset + new StringInfo(remaining).SubstringByTextElements(0, contentBudget).Length;
            var boundary = text.LastIndexOf("\n\n", end - 1, end - offset, StringComparison.Ordinal);
            if (boundary <= offset)
            {
                chunks.Add(text[offset..end]);
                offset = end;
                continue;
            }

            chunks.Add(text[offset..boundary]);
            offset = boundary + 2;
        }

        return chunks;
    }

    private static bool IsLarkPlatform(string normalizedPlatform) =>
        normalizedPlatform is "lark" or "feishu";
}
