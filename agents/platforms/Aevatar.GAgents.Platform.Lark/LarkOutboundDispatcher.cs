using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Sends new Lark messages through the NyxID proxy and returns typed delivery outcomes.
/// </summary>
/// <remarks>
/// Refactor (iter166/cluster-415-lark-outbound-dispatcher):
///   Old pattern: three callers duplicated Lark POST body construction, bot-not-in-chat fallback retry, and message_id parsing.
///   New principle: this dispatcher centralizes new-message delivery so callers map business state into a request and handle the typed result.
/// </remarks>
public sealed class LarkOutboundDispatcher : ILarkOutboundDispatcher
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public LarkOutboundDispatcher(
        NyxIdApiClient client,
        ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<LarkSendNewMessageResult> SendNewMessageAsync(
        LarkSendNewMessageRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var primaryResult = await SendPostAsync(
            request,
            request.PrimaryTarget,
            usedFallback: false,
            ct).ConfigureAwait(false);
        if (primaryResult.Succeeded)
            return primaryResult;

        if (primaryResult.LarkCode != LarkBotErrorCodes.BotNotInChat ||
            request.FallbackTarget is not { } fallbackTarget)
        {
            return primaryResult;
        }

        _logger.LogInformation(
            "Lark outbound primary target rejected as `bot not in chat` (230002); retrying once with fallback receive_id_type={FallbackType}",
            fallbackTarget.ReceiveIdType);

        return await SendPostAsync(
            request,
            fallbackTarget,
            usedFallback: true,
            ct).ConfigureAwait(false);
    }

    public async Task<LarkUpdateMessageResult> UpdateMessageAsync(
        LarkUpdateMessageRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var body = JsonSerializer.Serialize(new
        {
            msg_type = request.MessageType,
            content = request.ContentJson,
        });

        var response = await _client.ProxyRequestAsync(
            request.NyxApiKey,
            request.NyxProviderSlug,
            $"open-apis/im/v1/messages/{Uri.EscapeDataString(request.MessageId)}",
            "PUT",
            body,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return LarkUpdateMessageResult.Failed(larkCode, detail);

        return LarkUpdateMessageResult.Updated(request.MessageId);
    }

    private async Task<LarkSendNewMessageResult> SendPostAsync(
        LarkSendNewMessageRequest request,
        LarkReceiveTarget target,
        bool usedFallback,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            receive_id = target.ReceiveId,
            msg_type = request.MessageType,
            content = request.ContentJson,
        });

        var response = await _client.ProxyRequestAsync(
            request.NyxApiKey,
            request.NyxProviderSlug,
            $"open-apis/im/v1/messages?receive_id_type={Uri.EscapeDataString(target.ReceiveIdType)}",
            "POST",
            body,
            extraHeaders: null,
            ct).ConfigureAwait(false);

        return ParseSendResponse(response, target, usedFallback);
    }

    private static LarkSendNewMessageResult ParseSendResponse(
        string? response,
        LarkReceiveTarget target,
        bool usedFallback)
    {
        if (LarkProxyResponse.TryGetError(response, out var larkCode, out var detail))
            return LarkSendNewMessageResult.Failed(target, usedFallback, larkCode, detail);

        if (string.IsNullOrWhiteSpace(response))
            return LarkSendNewMessageResult.Failed(target, usedFallback, null, "empty_send_response");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return LarkSendNewMessageResult.Failed(target, usedFallback, null, "missing_data");
            if (!data.TryGetProperty("message_id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                return LarkSendNewMessageResult.Failed(target, usedFallback, null, "missing_message_id");
            }

            var messageId = idProperty.GetString();
            if (string.IsNullOrWhiteSpace(messageId))
                return LarkSendNewMessageResult.Failed(target, usedFallback, null, "empty_message_id");

            return LarkSendNewMessageResult.Sent(messageId, target, usedFallback);
        }
        catch (JsonException)
        {
            return LarkSendNewMessageResult.Failed(target, usedFallback, null, "invalid_send_response_json");
        }
    }

    private static void Validate(LarkSendNewMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NyxApiKey))
            throw new ArgumentException("NyxID API key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.NyxProviderSlug))
            throw new ArgumentException("NyxID provider slug is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MessageType))
            throw new ArgumentException("Lark message type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ContentJson))
            throw new ArgumentException("Lark message content JSON is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PrimaryTarget.ReceiveId))
            throw new ArgumentException("Lark primary receive_id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PrimaryTarget.ReceiveIdType))
            throw new ArgumentException("Lark primary receive_id_type is required.", nameof(request));
    }

    private static void Validate(LarkUpdateMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NyxApiKey))
            throw new ArgumentException("NyxID API key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.NyxProviderSlug))
            throw new ArgumentException("NyxID provider slug is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MessageId))
            throw new ArgumentException("Lark message id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MessageType))
            throw new ArgumentException("Lark message type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ContentJson))
            throw new ArgumentException("Lark message content JSON is required.", nameof(request));
    }
}
