using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Delivers one scheduled skill output through Lark CardKit: create a streaming card shell,
/// bind it to the conversation as an interactive message, write the final markdown content,
/// then close streaming mode.
/// </summary>
internal sealed class SkillRunnerCardKitReplySink
{
    private const string StreamingElementId = "streaming_main";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NyxIdApiClient _client;
    private readonly ILarkOutboundDispatcher _outboundDispatcher;
    private readonly LarkSendNewMessageRequest _interactiveMessageTemplate;
    private readonly ILogger? _logger;

    public SkillRunnerCardKitReplySink(
        NyxIdApiClient client,
        ILarkOutboundDispatcher outboundDispatcher,
        LarkSendNewMessageRequest interactiveMessageTemplate,
        ILogger? logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _outboundDispatcher = outboundDispatcher ?? throw new ArgumentNullException(nameof(outboundDispatcher));
        _interactiveMessageTemplate = interactiveMessageTemplate ?? throw new ArgumentNullException(nameof(interactiveMessageTemplate));
        _logger = logger;
    }

    public async Task<SkillRunnerCardKitDeliveryResult> SendFinalAsync(string text, CancellationToken ct)
    {
        var content = SkillRunnerStreamingReplySink.TruncateForLark(text);
        if (string.IsNullOrWhiteSpace(content))
            return SkillRunnerCardKitDeliveryResult.Success(visibleMessageCreated: false);

        var create = await CreateCardAsync(ct).ConfigureAwait(false);
        if (!create.Succeeded)
            return create;

        var cardId = create.CardId!;
        var bind = await BindCardAsync(cardId, ct).ConfigureAwait(false);
        if (!bind.Succeeded)
            return bind;

        var stream = await StreamContentAsync(cardId, content, sequence: 1, ct).ConfigureAwait(false);
        if (!stream.Succeeded)
        {
            await TryCloseStreamingAsync(cardId, sequence: 2, ct).ConfigureAwait(false);
            return stream with { VisibleMessageCreated = true };
        }

        var close = await CloseStreamingAsync(cardId, sequence: 2, ct).ConfigureAwait(false);
        if (!close.Succeeded)
        {
            _logger?.LogWarning(
                "SkillRunner CardKit close-streaming failed after final content was delivered; treating the run as delivered to avoid duplicate cards. card_id={CardId}, lark_code={LarkCode}, detail={Detail}",
                cardId,
                close.LarkCode,
                close.Detail);
        }

        return SkillRunnerCardKitDeliveryResult.Success(visibleMessageCreated: true, cardId);
    }

    private async Task<SkillRunnerCardKitDeliveryResult> CreateCardAsync(CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["type"] = "card_json",
                    ["data"] = LarkStreamingCardShell.BuildInitialCardJson(StreamingElementId),
                },
                JsonOptions);

            var response = await _client.ProxyRequestAsync(
                _interactiveMessageTemplate.NyxApiKey,
                _interactiveMessageTemplate.NyxProviderSlug,
                "open-apis/cardkit/v1/cards",
                "POST",
                body,
                extraHeaders: null,
                ct).ConfigureAwait(false);

            if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
                return SkillRunnerCardKitDeliveryResult.Failed(false, null, larkCode, detail);

            var cardId = ExtractCardId(response);
            return string.IsNullOrWhiteSpace(cardId)
                ? SkillRunnerCardKitDeliveryResult.Failed(false, null, null, "card.create response did not include data.card_id")
                : SkillRunnerCardKitDeliveryResult.Success(false, cardId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SkillRunner CardKit create threw; scheduled output will fall back to text if no card is visible. slug={Slug}",
                _interactiveMessageTemplate.NyxProviderSlug);
            return SkillRunnerCardKitDeliveryResult.Failed(false, null, null, ex.Message);
        }
    }

    private async Task<SkillRunnerCardKitDeliveryResult> BindCardAsync(string cardId, CancellationToken ct)
    {
        var contentJson = JsonSerializer.Serialize(
            new { type = "card", data = new { card_id = cardId } },
            JsonOptions);

        try
        {
            var result = await _outboundDispatcher.SendNewMessageAsync(
                _interactiveMessageTemplate with
                {
                    MessageType = "interactive",
                    ContentJson = contentJson,
                },
                ct).ConfigureAwait(false);

            return result.Succeeded
                ? SkillRunnerCardKitDeliveryResult.Success(true, cardId)
                : SkillRunnerCardKitDeliveryResult.Failed(false, cardId, result.LarkCode, result.Detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SkillRunner CardKit bind-to-chat threw; scheduled output will fall back to text because no card message is visible. card_id={CardId}",
                cardId);
            return SkillRunnerCardKitDeliveryResult.Failed(false, cardId, null, ex.Message);
        }
    }

    private async Task<SkillRunnerCardKitDeliveryResult> StreamContentAsync(
        string cardId,
        string content,
        long sequence,
        CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["content"] = content,
                    ["sequence"] = sequence,
                    ["uuid"] = $"scheduled-final-{cardId}-{sequence}",
                },
                JsonOptions);

            var response = await _client.ProxyRequestAsync(
                _interactiveMessageTemplate.NyxApiKey,
                _interactiveMessageTemplate.NyxProviderSlug,
                $"open-apis/cardkit/v1/cards/{Uri.EscapeDataString(cardId)}/elements/{Uri.EscapeDataString(StreamingElementId)}/content",
                "PUT",
                body,
                extraHeaders: null,
                ct).ConfigureAwait(false);

            return LarkProxyResponse.TryGetError(response, out var larkCode, out var detail)
                ? SkillRunnerCardKitDeliveryResult.Failed(true, cardId, larkCode, detail)
                : SkillRunnerCardKitDeliveryResult.Success(true, cardId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SkillRunner CardKit final content update threw after the card became visible. card_id={CardId}",
                cardId);
            return SkillRunnerCardKitDeliveryResult.Failed(true, cardId, null, ex.Message);
        }
    }

    private async Task<SkillRunnerCardKitDeliveryResult> CloseStreamingAsync(
        string cardId,
        long sequence,
        CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["settings"] = LarkStreamingCardShell.BuildCloseStreamingSettingsJson(),
                    ["sequence"] = sequence,
                    ["uuid"] = $"scheduled-close-{cardId}-{sequence}",
                },
                JsonOptions);

            var response = await _client.ProxyRequestAsync(
                _interactiveMessageTemplate.NyxApiKey,
                _interactiveMessageTemplate.NyxProviderSlug,
                $"open-apis/cardkit/v1/cards/{Uri.EscapeDataString(cardId)}/settings",
                "PATCH",
                body,
                extraHeaders: null,
                ct).ConfigureAwait(false);

            return LarkProxyResponse.TryGetError(response, out var larkCode, out var detail)
                ? SkillRunnerCardKitDeliveryResult.Failed(true, cardId, larkCode, detail)
                : SkillRunnerCardKitDeliveryResult.Success(true, cardId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SkillRunner CardKit close-streaming threw after the card became visible. card_id={CardId}",
                cardId);
            return SkillRunnerCardKitDeliveryResult.Failed(true, cardId, null, ex.Message);
        }
    }

    private async Task TryCloseStreamingAsync(string cardId, long sequence, CancellationToken ct)
    {
        var close = await CloseStreamingAsync(cardId, sequence, ct).ConfigureAwait(false);
        if (!close.Succeeded)
        {
            _logger?.LogWarning(
                "SkillRunner CardKit best-effort close failed after content update failure. card_id={CardId}, lark_code={LarkCode}, detail={Detail}",
                cardId,
                close.LarkCode,
                close.Detail);
        }
    }

    private static string? ExtractCardId(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("card_id", out var cardIdProperty) &&
                cardIdProperty.ValueKind == JsonValueKind.String)
            {
                return cardIdProperty.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

internal sealed record SkillRunnerCardKitDeliveryResult(
    bool Succeeded,
    bool VisibleMessageCreated,
    string? CardId,
    int? LarkCode,
    string Detail)
{
    public static SkillRunnerCardKitDeliveryResult Success(
        bool visibleMessageCreated,
        string? cardId = null) =>
        new(true, visibleMessageCreated, cardId, null, string.Empty);

    public static SkillRunnerCardKitDeliveryResult Failed(
        bool visibleMessageCreated,
        string? cardId,
        int? larkCode,
        string detail) =>
        new(false, visibleMessageCreated, cardId, larkCode, detail);
}
