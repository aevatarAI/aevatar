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
/// <see cref="ILarkNyxClient.SendMessageAsync"/> (im/v1/messages with msg_type=interactive)
/// to drive the create → send → stream → finalize lifecycle. Auth: bot owner's NyxID
/// access token from <c>activity.TransportExtras.NyxUserAccessToken</c>; receive target:
/// <c>nyx_lark_chat_id</c> for groups, falling back to <c>nyx_lark_union_id</c> for p2p
/// DMs (cross-app safe per the proto's documented invariants).
/// </summary>
public sealed class ChannelCardConversationTurnRunner : IConversationCardTurnRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly ILarkCardKitClient _cardKit;
    private readonly ILarkNyxClient _larkClient;
    private readonly ILogger<ChannelCardConversationTurnRunner> _logger;

    public ChannelCardConversationTurnRunner(
        ILarkCardKitClient cardKit,
        ILarkNyxClient larkClient,
        ILogger<ChannelCardConversationTurnRunner> logger)
    {
        _cardKit = cardKit ?? throw new ArgumentNullException(nameof(cardKit));
        _larkClient = larkClient ?? throw new ArgumentNullException(nameof(larkClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConversationCardCreateResult> RunCardCreateAsync(
        LlmReplyStreamChunkEvent chunk,
        string streamingElementId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Activity is null)
            return ConversationCardCreateResult.Failed("activity_required", "Stream chunk event is missing the source activity.");

        var token = ResolveToken(chunk.Activity);
        if (token is null)
            return ConversationCardCreateResult.Failed("token_missing", "NyxID user access token is missing on the activity's TransportExtras.");

        var receiveTarget = ResolveReceiveTarget(chunk.Activity);
        if (receiveTarget is null)
            return ConversationCardCreateResult.Failed("receive_target_missing", "Lark chat_id and union_id are both missing on TransportExtras.");

        // 1. Allocate a CardKit entity holding an empty streaming element. The first chunk's
        //    text lands via StreamElementContentAsync (step 3) so the card_json schema and
        //    the streaming wire format stay decoupled.
        var initialCardJson = LarkStreamingCardShell.BuildInitialCardJson(streamingElementId);
        string createResponse;
        try
        {
            createResponse = await _cardKit.CreateCardAsync(
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

        // 2. Bind the card to the chat by sending an interactive message that references it.
        var contentJson = JsonSerializer.Serialize(
            new { type = "card", data = new { card_id = cardId } },
            JsonOptions);
        string sendResponse;
        try
        {
            sendResponse = await _larkClient.SendMessageAsync(
                token,
                new LarkSendMessageRequest(
                    TargetType: receiveTarget.Value.ReceiveIdType,
                    TargetId: receiveTarget.Value.ReceiveId,
                    MessageType: "interactive",
                    ContentJson: contentJson,
                    IdempotencyKey: chunk.CorrelationId),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Card send-to-chat threw for correlation={CorrelationId}, card_id={CardId}", chunk.CorrelationId, cardId);
            return ConversationCardCreateResult.Failed("card_send_threw", ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(sendResponse, out var sendError))
            return ClassifyCreateFailure("card_send_failed", sendError);

        var cardMessageId = LarkProxyResponseParser.ParseSendSuccess(sendResponse).MessageId
            ?? string.Empty;

        // 3. Write the first chunk's text into the streaming element. Sequence = 1 (the
        //    grain pre-allocates this value; subsequent chunks pass sequence+1 each call).
        string firstStreamResponse;
        try
        {
            firstStreamResponse = await _cardKit.StreamElementContentAsync(
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
            return ConversationCardCreateResult.Failed("card_first_stream_threw", ex.Message);
        }

        if (LarkProxyResponseParser.TryParseError(firstStreamResponse, out var firstStreamError))
            return ClassifyCreateFailure("card_first_stream_failed", firstStreamError);

        return ConversationCardCreateResult.Succeeded(cardId, cardMessageId);
    }

    public async Task<ConversationCardStreamResult> RunCardStreamAsync(
        LlmReplyStreamChunkEvent chunk,
        string cardId,
        string elementId,
        long sequence,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Activity is null)
            return ConversationCardStreamResult.Failed("activity_required", "Stream chunk event is missing the source activity.");

        var token = ResolveToken(chunk.Activity);
        if (token is null)
            return ConversationCardStreamResult.Failed("token_missing", "NyxID user access token is missing on the activity's TransportExtras.");

        string response;
        try
        {
            response = await _cardKit.StreamElementContentAsync(
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

        var token = ResolveToken(referenceActivity);
        if (token is null)
            return ConversationCardFinalizeResult.Failed("token_missing", "NyxID user access token is missing on the reference activity's TransportExtras.");

        // 1. If final text drifted from the last flushed interim, write it before closing
        //    streaming mode. Order matters: closing streaming first would freeze the cursor
        //    on the stale text.
        long workingSequence = sequence;
        if (finalTextDiffersFromLastFlushed && !string.IsNullOrWhiteSpace(finalText))
        {
            try
            {
                var streamFinalResponse = await _cardKit.StreamElementContentAsync(
                    token,
                    new LarkCardKitStreamElementContentRequest(
                        CardId: cardId,
                        ElementId: elementId,
                        Content: finalText,
                        Sequence: workingSequence,
                        IdempotencyKey: $"final-{cardId}-{workingSequence}"),
                    ct);
                if (LarkProxyResponseParser.TryParseError(streamFinalResponse, out var streamFinalError))
                    return ConversationCardFinalizeResult.Failed("card_final_stream_failed", streamFinalError);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CardKit final stream threw for card_id={CardId}, seq={Sequence}", cardId, workingSequence);
                return ConversationCardFinalizeResult.Failed("card_final_stream_threw", ex.Message);
            }
            workingSequence++;
        }

        // 2. Close the card's streaming mode so the typewriter cursor disappears.
        try
        {
            var settingsResponse = await _cardKit.SetCardSettingsAsync(
                token,
                new LarkCardKitSettingsRequest(
                    CardId: cardId,
                    SettingsJson: """{"streaming_mode": false}""",
                    Sequence: workingSequence,
                    IdempotencyKey: $"close-{cardId}-{workingSequence}"),
                ct);
            if (LarkProxyResponseParser.TryParseError(settingsResponse, out var settingsError))
                return ConversationCardFinalizeResult.Failed("card_close_streaming_failed", settingsError);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardKit close-streaming threw for card_id={CardId}, seq={Sequence}", cardId, workingSequence);
            return ConversationCardFinalizeResult.Failed("card_close_streaming_threw", ex.Message);
        }

        return ConversationCardFinalizeResult.Succeeded();
    }

    private static string? ResolveToken(ChatActivity activity)
    {
        var token = activity.TransportExtras?.NyxUserAccessToken?.Trim();
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
            isTableLimitExceeded: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 11310),
            isCardUnavailable: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 230100));

    private static ConversationCardStreamResult ClassifyStreamFailure(string larkError) =>
        ConversationCardStreamResult.Failed(
            errorCode: "card_stream_failed",
            errorSummary: larkError,
            isRateLimited: ContainsLarkCode(larkError, 230020),
            isTableLimitExceeded: ContainsLarkCode(larkError, 230099) || ContainsLarkCode(larkError, 11310),
            isCardUnavailable: ContainsLarkCode(larkError, 230100));

    /// <summary>
    /// Substring match against <see cref="LarkProxyResponseParser.TryParseError"/>'s output
    /// shape (<c>"lark_code={n} ..."</c>). Cheap, allocation-free; the parser owns the
    /// canonical error string format so this stays stable.
    /// </summary>
    private static bool ContainsLarkCode(string error, int code) =>
        !string.IsNullOrEmpty(error) && error.Contains($"lark_code={code}", StringComparison.Ordinal);
}
