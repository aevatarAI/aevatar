using System.Text.Json;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Production <see cref="IConversationCardTurnRunner"/> for the Lark CardKit streaming
/// path. Composes <see cref="ILarkCardKitClient"/> (cardkit/v1/* endpoints) with
/// <see cref="ILarkNyxClient.SendMessageAsync"/> or <see cref="ILarkNyxClient.ReplyToMessageAsync"/>
/// (im/v1/messages with msg_type=interactive) to drive the create → bind → stream → finalize lifecycle. Auth: bot owner's NyxID
/// access token from <c>activity.TransportExtras.NyxUserAccessToken</c>; receive target:
/// <c>nyx_lark_chat_id</c> for groups, falling back to <c>nyx_lark_union_id</c> for p2p
/// DMs (cross-app safe per the proto's documented invariants).
/// </summary>
public sealed class ChannelCardConversationTurnRunner : IConversationCardTurnRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly ILarkCardKitClient _cardKit;
    private readonly ILarkNyxClient _larkClient;
    private readonly ILarkOutboundClientFactory? _outboundClientFactory;
    private readonly ILogger<ChannelCardConversationTurnRunner> _logger;

    public ChannelCardConversationTurnRunner(
        ILarkCardKitClient cardKit,
        ILarkNyxClient larkClient,
        ILogger<ChannelCardConversationTurnRunner> logger)
        : this(cardKit, larkClient, outboundClientFactory: null, logger)
    {
    }

    public ChannelCardConversationTurnRunner(
        ILarkCardKitClient cardKit,
        ILarkNyxClient larkClient,
        ILarkOutboundClientFactory? outboundClientFactory,
        ILogger<ChannelCardConversationTurnRunner> logger)
    {
        _cardKit = cardKit ?? throw new ArgumentNullException(nameof(cardKit));
        _larkClient = larkClient ?? throw new ArgumentNullException(nameof(larkClient));
        _outboundClientFactory = outboundClientFactory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Resolves the CardKit/im clients bound to the inbound bot's outbound proxy slug (carried on the
    // reply activity's TransportExtras). Falls back to the configured-default singletons when no
    // factory is wired or the activity has no slug. This is what makes a card reply proxy through the
    // bot that received the inbound turn instead of the process-wide default `api-lark-bot`.
    private (ILarkCardKitClient CardKit, ILarkNyxClient Lark) ResolveOutboundClients(ChatActivity? activity)
    {
        var slug = activity?.TransportExtras?.NyxProviderSlug?.Trim();
        if (_outboundClientFactory is null || string.IsNullOrEmpty(slug))
            return (_cardKit, _larkClient);

        return (_outboundClientFactory.ResolveCardKitClient(slug),
                _outboundClientFactory.ResolveNyxClient(slug));
    }

    public async Task<ConversationCardCreateResult> RunCardCreateAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Activity is null)
            return ConversationCardCreateResult.Failed("activity_required", "Stream chunk event is missing the source activity.");

        var token = ResolveToken(chunk.Activity, runtimeContext);
        if (token is null)
            return ConversationCardCreateResult.Failed("token_missing", "NyxID user access token is missing on the activity's TransportExtras.");

        var (cardKit, larkClient) = ResolveOutboundClients(chunk.Activity);

        // 1. Allocate a CardKit entity holding an empty streaming element. The first chunk's
        //    text lands via StreamElementContentAsync (step 3) so the card_json schema and
        //    the streaming wire format stay decoupled.
        var initialCardJson = LarkStreamingCardShell.BuildInitialCardJson(streamingElementId);
        string createResponse;
        try
        {
            createResponse = await cardKit.CreateCardAsync(
                token,
                new LarkCardKitCreateRequest("card_json", initialCardJson),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardKit card.create threw for correlation={CorrelationId}", chunk.CorrelationId);
            return ConversationCardCreateResult.Failed("card_create_threw", ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(createResponse, out var createError))
            return ClassifyCreateFailure("card_create_failed", createError);

        var cardId = ExtractCardId(createResponse);
        if (string.IsNullOrWhiteSpace(cardId))
            return ConversationCardCreateResult.Failed("card_id_missing", "card.create response did not include data.card_id.");

        // 2. Bind the card to the chat by sending or replying with an interactive message
        //    that references it.
        var contentJson = JsonSerializer.Serialize(
            new { type = "card", data = new { card_id = cardId } },
            JsonOptions);
        var bindResult = await BindCardToConversationAsync(larkClient, token, chunk, cardId, contentJson, ct);
        if (!bindResult.Success)
            return bindResult;

        var cardMessageId = bindResult.CardMessageId ?? string.Empty;

        // 3. Write the first chunk's text into the streaming element. Sequence = 1 (the
        //    grain pre-allocates this value; subsequent chunks pass sequence+1 each call).
        //    The card has already been bound to the chat (step 2), so any failure from here
        //    on is a *post-send* failure: an empty card is visible in the chat. We must
        //    return PostSendFailed (not Failed) so the actor terminates the turn instead
        //    of falling back to text-edit and producing a duplicate reply.
        string firstStreamResponse;
        try
        {
            firstStreamResponse = await cardKit.StreamElementContentAsync(
                token,
                new LarkCardKitStreamElementContentRequest(
                    CardId: cardId,
                    ElementId: streamingElementId,
                    Content: chunk.AccumulatedText,
                    Sequence: 1,
                    IdempotencyKey: $"{chunk.CorrelationId}-1"),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardKit first stream threw for correlation={CorrelationId}, card_id={CardId}", chunk.CorrelationId, cardId);
            await TryBestEffortCloseStreamingAsync(cardKit, token, cardId, sequence: 2, ct).ConfigureAwait(false);
            return ConversationCardCreateResult.PostSendFailed(
                cardId,
                cardMessageId,
                "card_first_stream_threw",
                ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(firstStreamResponse, out var firstStreamError))
        {
            await TryBestEffortCloseStreamingAsync(cardKit, token, cardId, sequence: 2, ct).ConfigureAwait(false);
            return ClassifyPostSendFailure(cardId, cardMessageId, "card_first_stream_failed", firstStreamError);
        }

        return ConversationCardCreateResult.Succeeded(cardId, cardMessageId);
    }

    /// <summary>
    /// Best-effort settings patch to close <c>streaming_mode</c> on a card whose first
    /// content write failed. Stops the typewriter cursor on the orphan empty card so the
    /// chat does not show a perpetually-loading bubble. Failures are logged and swallowed —
    /// the parent operation has already failed; this is a UX cleanup, not a correctness gate.
    /// </summary>
    private async Task TryBestEffortCloseStreamingAsync(ILarkCardKitClient cardKit, string token, string cardId, long sequence, CancellationToken ct)
    {
        try
        {
            await cardKit.SetCardSettingsAsync(
                token,
                new LarkCardKitSettingsRequest(
                    CardId: cardId,
                    SettingsJson: LarkStreamingCardShell.BuildCloseStreamingSettingsJson(),
                    Sequence: sequence,
                    IdempotencyKey: $"orphan-close-{cardId}"),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort close of orphan streaming card failed; cursor may remain visible. card_id={CardId}", cardId);
        }
    }

    public async Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyCardStreamChunkEvent chunk,
        string cardId,
        string elementId,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Activity is null)
            return ConversationCardStreamResult.Failed("activity_required", "Stream chunk event is missing the source activity.");

        var token = ResolveToken(chunk.Activity, runtimeContext);
        if (token is null)
            return ConversationCardStreamResult.Failed("token_missing", "NyxID user access token is missing on the activity's TransportExtras.");

        var (cardKit, _) = ResolveOutboundClients(chunk.Activity);

        string response;
        try
        {
            response = await cardKit.StreamElementContentAsync(
                token,
                new LarkCardKitStreamElementContentRequest(
                    CardId: cardId,
                    ElementId: elementId,
                    Content: chunk.AccumulatedText,
                    Sequence: sequence,
                    IdempotencyKey: $"{chunk.CorrelationId}-{sequence}"),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardKit interim stream threw for correlation={CorrelationId}, card_id={CardId}, seq={Sequence}", chunk.CorrelationId, cardId, sequence);
            return ConversationCardStreamResult.Failed("card_stream_threw", ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return ClassifyStreamFailure(error);

        return ConversationCardStreamResult.Succeeded();
    }

    public async Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
        ChatActivity referenceActivity,
        string cardId,
        string elementId,
        string finalText,
        bool finalTextDiffersFromLastFlushed,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(referenceActivity);

        var token = ResolveToken(referenceActivity, runtimeContext);
        if (token is null)
            return ConversationCardFinalizeResult.Failed("token_missing", "NyxID user access token is missing on the reference activity's TransportExtras.");

        var (cardKit, _) = ResolveOutboundClients(referenceActivity);

        // 1. If final text drifted from the last flushed interim, write it before closing
        //    streaming mode. Order matters: closing streaming first would freeze the cursor
        //    on the stale text. Track whether the trailing write actually landed so the
        //    actor can pick the right user-visible text on a partial-failure terminal.
        long workingSequence = sequence;
        var finalTextWritten = !finalTextDiffersFromLastFlushed || string.IsNullOrWhiteSpace(finalText);
        if (finalTextDiffersFromLastFlushed && !string.IsNullOrWhiteSpace(finalText))
        {
            try
            {
                var streamFinalResponse = await cardKit.StreamElementContentAsync(
                    token,
                    new LarkCardKitStreamElementContentRequest(
                        CardId: cardId,
                        ElementId: elementId,
                        Content: finalText,
                        Sequence: workingSequence,
                        IdempotencyKey: $"final-{cardId}-{workingSequence}"),
                    ct);
                if (LarkProxyResponseParser.TryParseError(streamFinalResponse, out var streamFinalError))
                    return ConversationCardFinalizeResult.Failed("card_final_stream_failed", streamFinalError, finalTextWritten: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CardKit final stream threw for card_id={CardId}, seq={Sequence}", cardId, workingSequence);
                return ConversationCardFinalizeResult.Failed("card_final_stream_threw", ex.Message, finalTextWritten: false);
            }
            finalTextWritten = true;
            workingSequence++;
        }

        // 2. Close the card's streaming mode so the typewriter cursor disappears.
        try
        {
            var settingsResponse = await cardKit.SetCardSettingsAsync(
                token,
                new LarkCardKitSettingsRequest(
                    CardId: cardId,
                    SettingsJson: LarkStreamingCardShell.BuildCloseStreamingSettingsJson(),
                    Sequence: workingSequence,
                    IdempotencyKey: $"close-{cardId}-{workingSequence}"),
                ct);
            if (LarkProxyResponseParser.TryParseError(settingsResponse, out var settingsError))
                return ConversationCardFinalizeResult.Failed("card_close_streaming_failed", settingsError, finalTextWritten: finalTextWritten);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardKit close-streaming threw for card_id={CardId}, seq={Sequence}", cardId, workingSequence);
            return ConversationCardFinalizeResult.Failed("card_close_streaming_threw", ex.Message, finalTextWritten: finalTextWritten);
        }

        return ConversationCardFinalizeResult.Succeeded();
    }

    // Refactor (iter17/cluster-038):
    //   Old pattern: CardKit calls required the Nyx user token to remain on persisted activity/reference activity transport extras.
    //   New principle: CardKit create/stream/finalize accept sanitized activities and resolve the token from per-turn runtime context.
    private static string? ResolveToken(
        ChatActivity activity,
        ConversationTurnRuntimeContext runtimeContext)
    {
        var token = activity.TransportExtras?.NyxUserAccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            token = runtimeContext.NyxUserAccessToken?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static (string ReceiveIdType, string ReceiveId)? ResolveReceiveTarget(ChatActivity activity)
    {
        // Group / channel / thread: the relay-side chat_id is cross-app safe within the tenant.
        var chatId = activity.TransportExtras?.NyxLarkChatId?.Trim();
        var conversationScope = activity.Conversation?.Scope ?? ConversationScope.Unspecified;
        var isGroupLike = conversationScope is ConversationScope.Group
                                            or ConversationScope.Channel
                                            or ConversationScope.Thread;
        if (isGroupLike && !string.IsNullOrWhiteSpace(chatId))
            return ("chat_id", chatId);

        // Direct message: the chat_id is bot-specific and not cross-app safe; prefer union_id.
        var unionId = activity.TransportExtras?.NyxLarkUnionId?.Trim();
        if (!string.IsNullOrWhiteSpace(unionId))
            return ("union_id", unionId);

        // Fall back to chat_id for DMs only when union_id is unavailable. The relay populates
        // union_id whenever it can resolve it, so this branch generally does not fire.
        if (!string.IsNullOrWhiteSpace(chatId))
            return ("chat_id", chatId);

        return null;
    }

    private async Task<ConversationCardCreateResult> BindCardToConversationAsync(
        ILarkNyxClient larkClient,
        string token,
        LlmReplyCardStreamChunkEvent chunk,
        string cardId,
        string contentJson,
        CancellationToken ct)
    {
        var inboundMessageId = ResolveInboundMessageId(chunk.Activity);
        var isGroupLike = chunk.Activity?.Conversation?.Scope is ConversationScope.Group
                                                        or ConversationScope.Channel
                                                        or ConversationScope.Thread;
        var shouldReplyInThread = isGroupLike && !string.IsNullOrWhiteSpace(inboundMessageId);
        string response;
        try
        {
            if (shouldReplyInThread)
            {
                response = await larkClient.ReplyToMessageAsync(
                    token,
                    new LarkReplyMessageRequest(
                        MessageId: inboundMessageId!,
                        MessageType: "interactive",
                        ContentJson: contentJson,
                        ReplyInThread: true,
                        IdempotencyKey: chunk.CorrelationId),
                    ct);
            }
            else
            {
                var receiveTarget = ResolveReceiveTarget(chunk.Activity!);
                if (receiveTarget is null)
                    return ConversationCardCreateResult.Failed("receive_target_missing", "Lark chat_id and union_id are both missing on TransportExtras.");

                response = await larkClient.SendMessageAsync(
                    token,
                    new LarkSendMessageRequest(
                        TargetType: receiveTarget.Value.ReceiveIdType,
                        TargetId: receiveTarget.Value.ReceiveId,
                        MessageType: "interactive",
                        ContentJson: contentJson,
                        IdempotencyKey: chunk.CorrelationId),
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Card bind-to-chat threw for correlation={CorrelationId}, card_id={CardId}", chunk.CorrelationId, cardId);
            return ConversationCardCreateResult.Failed("card_send_threw", ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(response, out var sendError))
            return ClassifyCreateFailure("card_send_failed", sendError);

        var cardMessageId = LarkProxyResponseParser.ParseSendSuccess(response).MessageId
            ?? string.Empty;
        return ConversationCardCreateResult.Succeeded(cardId, cardMessageId);
    }

    private static string? ResolveInboundMessageId(ChatActivity? activity)
    {
        var platformMessageId = activity?.TransportExtras?.NyxPlatformMessageId?.Trim();
        if (string.IsNullOrWhiteSpace(platformMessageId) ||
            !platformMessageId.StartsWith("om_", StringComparison.Ordinal))
        {
            return null;
        }

        return platformMessageId;
    }

    /// <summary>
    /// Best-effort extract of <c>data.card_id</c> from the <c>cardkit/v1/cards</c> response.
    /// Returns null when the field is missing or malformed; the caller treats null as a
    /// terminal create failure.
    /// </summary>
    private static string? ExtractCardId(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("card_id", out var cardIdProp) &&
                cardIdProp.ValueKind == JsonValueKind.String)
            {
                return cardIdProp.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static ConversationCardCreateResult ClassifyCreateFailure(string contextErrorCode, string larkError) =>
        ConversationCardCreateResult.Failed(
            errorCode: contextErrorCode,
            errorSummary: larkError,
            isRateLimited: ContainsLarkCode(larkError, 230020),
            isTableLimitExceeded: ContainsLarkCode(larkError, 11310),
            isCardUnavailable: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 230100));

    /// <summary>
    /// Same classification as <see cref="ClassifyCreateFailure"/> but threads the
    /// already-allocated <paramref name="cardId"/> / <paramref name="cardMessageId"/> through
    /// the result so the actor can persist the partial-card terminal record. Used for any
    /// failure that occurs after <c>im/v1/messages</c> has bound the card to the chat.
    /// </summary>
    private static ConversationCardCreateResult ClassifyPostSendFailure(
        string cardId,
        string cardMessageId,
        string contextErrorCode,
        string larkError) =>
        ConversationCardCreateResult.PostSendFailed(
            cardId: cardId,
            cardMessageId: cardMessageId,
            errorCode: contextErrorCode,
            errorSummary: larkError,
            isRateLimited: ContainsLarkCode(larkError, 230020),
            isTableLimitExceeded: ContainsLarkCode(larkError, 11310),
            isCardUnavailable: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 230100));

    private static ConversationCardStreamResult ClassifyStreamFailure(string larkError) =>
        ConversationCardStreamResult.Failed(
            errorCode: "card_stream_failed",
            errorSummary: larkError,
            isRateLimited: ContainsLarkCode(larkError, 230020),
            isTableLimitExceeded: ContainsLarkCode(larkError, 11310),
            isCardUnavailable: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 230100));

    /// <summary>
    /// Boundary-aware match against <see cref="LarkProxyResponseParser.TryParseError"/>'s
    /// output shape (<c>"lark_code={n} ..."</c>). The needle's trailing position must be
    /// the end of the string OR a non-digit; without the boundary check, looking for
    /// <c>lark_code=23002</c> would falsely match a string containing <c>lark_code=230020</c>.
    /// </summary>
    private static bool ContainsLarkCode(string error, int code)
    {
        if (string.IsNullOrEmpty(error))
            return false;
        var needle = $"lark_code={code}";
        var index = 0;
        while (index <= error.Length - needle.Length)
        {
            var found = error.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0)
                return false;
            var endIndex = found + needle.Length;
            if (endIndex == error.Length || !char.IsDigit(error[endIndex]))
                return true;
            index = endIndex;
        }
        return false;
    }
}
