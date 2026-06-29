using System.Collections.ObjectModel;
using System.Globalization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayOutboundPort
{
    internal const int LarkRelayTextMessageLimit = 30_000;

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

        var normalizedPlatform = NormalizePlatformKey(platform);
        if (TryComposeReplyPayload(platform, conversation, content, out var replyPayload) is { } composeFailure)
        {
            return composeFailure;
        }

        if (ValidateRelayPayloadSize(normalizedPlatform, replyPayload, allowRichPayload: true) is { } sizeFailure)
        {
            return sizeFailure;
        }

        var sendResult = await SendRelayReplyAsync(
                normalizedPlatform,
                replyToken,
                delivery.ReplyMessageId,
                replyPayload,
                ct)
            .ConfigureAwait(false);
        if (!sendResult.Succeeded)
        {
            _logger.LogWarning(
                "Nyx relay reply delivery failed: platform={Platform}, messageId={MessageId}, detail={Detail}",
                platform,
                delivery.ReplyMessageId,
                sendResult.Detail);
            return EmitResult.Failed(
                "relay_reply_rejected",
                sendResult.Detail ?? "Nyx relay reply rejected.");
        }

        return EmitResult.Sent(
            sendResult.MessageId ?? $"nyx-relay:{delivery.ReplyMessageId}",
            platformMessageId: sendResult.PlatformMessageId);
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

        if (TryComposeReplyPayload(platform, conversation, content, out var replyPayload) is { } composeFailure)
        {
            return composeFailure;
        }

        var normalizedPlatform = NormalizePlatformKey(platform);
        if (ValidateRelayPayloadSize(normalizedPlatform, replyPayload, allowRichPayload: false) is { } sizeFailure)
        {
            return sizeFailure;
        }

        var result = await _nyxClient.UpdateChannelRelayTextReplyAsync(
            replyToken,
            platformMessageId,
            replyPayload.Text,
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

    private EmitResult? TryComposeReplyPayload(
        string platform,
        ConversationReference conversation,
        MessageContent content,
        out RelayReplyPayload replyPayload)
    {
        replyPayload = new RelayReplyPayload(string.Empty, CardPayload: null);
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

            var composed = composer.Compose(content, composeContext);
            if (composed is not IPlainTextComposedMessage plainTextPayload)
            {
                return EmitResult.Failed(
                    "plain_text_payload_unavailable",
                    $"Relay outbound composer for platform '{normalizedPlatform}' does not expose a plain-text payload.");
            }

            replyPayload = new RelayReplyPayload(
                plainTextPayload.PlainText,
                TryBuildRelayCardPayload(normalizedPlatform, composed));
        }
        else
        {
            replyPayload = new RelayReplyPayload(NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(content), CardPayload: null);
        }

        if (string.IsNullOrWhiteSpace(replyPayload.Text))
        {
            return EmitResult.Failed(
                "empty_reply",
                "Relay outbound could not render a non-empty reply payload.");
        }

        return null;
    }

    private async Task<NyxIdChannelRelayReplyResult> SendRelayReplyAsync(
        string normalizedPlatform,
        string replyToken,
        string replyMessageId,
        RelayReplyPayload replyPayload,
        CancellationToken ct)
    {
        var body = BuildRelayReplyBody(normalizedPlatform, replyPayload);
        return await _nyxClient.SendChannelRelayReplyAsync(
                    replyToken,
                    replyMessageId,
                    body,
                    ct)
                .ConfigureAwait(false);
    }

    private static int ResolveRelayTextLimit(string normalizedPlatform) =>
        string.Equals(normalizedPlatform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(normalizedPlatform, "feishu", StringComparison.OrdinalIgnoreCase)
            ? LarkRelayTextMessageLimit
            : 0;

    private static EmitResult? ValidateRelayPayloadSize(
        string normalizedPlatform,
        RelayReplyPayload replyPayload,
        bool allowRichPayload)
    {
        var limit = ResolveRelayTextLimit(normalizedPlatform);
        if (limit <= 0 || new StringInfo(replyPayload.Text).LengthInTextElements <= limit)
        {
            return null;
        }

        if (allowRichPayload && replyPayload.CardPayload is not null)
        {
            return null;
        }

        return EmitResult.Failed(
            "relay_reply_text_too_long",
            $"Relay reply text exceeds the {limit} text-element limit for platform '{normalizedPlatform}'.",
            capability: ComposeCapability.Degraded);
    }

    private static ChannelRelayReplyBody BuildRelayReplyBody(string normalizedPlatform, RelayReplyPayload replyPayload)
    {
        var limit = ResolveRelayTextLimit(normalizedPlatform);
        if (limit > 0 &&
            new StringInfo(replyPayload.Text).LengthInTextElements > limit &&
            replyPayload.CardPayload is not null)
        {
            return new ChannelRelayReplyBody(
                Text: replyPayload.Text,
                Metadata: new ChannelRelayReplyMetadata(replyPayload.CardPayload));
        }

        return new ChannelRelayReplyBody(replyPayload.Text);
    }

    private static object? TryBuildRelayCardPayload(string normalizedPlatform, object composed)
    {
        if (!IsLarkPlatform(normalizedPlatform) ||
            composed is not IInteractiveComposedMessage { IsInteractive: true } interactive ||
            string.IsNullOrWhiteSpace(interactive.ContentJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(interactive.ContentJson);
            return document.RootElement.Clone();
        }
        catch (System.Text.Json.JsonException)
        {
            return interactive.ContentJson;
        }
    }

    private static bool IsLarkPlatform(string normalizedPlatform) =>
        string.Equals(normalizedPlatform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(normalizedPlatform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePlatformKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private readonly record struct RelayReplyPayload(string Text, object? CardPayload);
}
