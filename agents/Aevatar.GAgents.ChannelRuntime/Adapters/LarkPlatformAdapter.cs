using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Adapters;

/// <summary>
/// Platform adapter for Lark (Feishu) bot callbacks.
/// Handles URL verification challenges and im.message.receive_v1 events.
/// Outbound replies go through Nyx's api-lark-bot provider.
/// </summary>
public sealed class LarkPlatformAdapter : IPlatformAdapter
{
    private readonly ILogger<LarkPlatformAdapter> _logger;

    public LarkPlatformAdapter(ILogger<LarkPlatformAdapter> logger) => _logger = logger;

    public string Platform => "lark";

    public async Task<IResult?> TryHandleVerificationAsync(
        HttpContext http, ChannelBotRegistrationEntry registration)
    {
        http.Request.EnableBuffering();
        http.Request.Body.Position = 0;

        using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        http.Request.Body.Position = 0;

        var root = doc.RootElement;

        // Lark URL verification challenge
        if (root.TryGetProperty("type", out var typeProp) &&
            typeProp.GetString() == "url_verification")
        {
            // Verify the token matches the registration before echoing the challenge.
            // Without this check, any caller who can reach the callback URL could
            // forge Lark payloads and drive bot traffic.
            var incomingToken = root.TryGetProperty("token", out var tokenProp)
                ? tokenProp.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(registration.VerificationToken) &&
                !string.Equals(incomingToken, registration.VerificationToken, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Lark URL verification token mismatch — rejecting challenge");
                return Results.Unauthorized();
            }

            var challenge = root.TryGetProperty("challenge", out var ch) ? ch.GetString() : null;
            _logger.LogInformation("Lark URL verification challenge accepted");
            return Results.Json(new { challenge });
        }

        return null;
    }

    public async Task<InboundMessage?> ParseInboundAsync(
        HttpContext http, ChannelBotRegistrationEntry registration)
    {
        http.Request.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var root = doc.RootElement;

        // Lark v2 event callback format:
        // { "schema": "2.0", "header": { "event_type": "im.message.receive_v1", ... },
        //   "event": { "sender": { "sender_id": { "open_id": "..." } },
        //              "message": { "chat_id": "...", "message_type": "text",
        //                           "content": "{\"text\":\"hello\"}", "message_id": "..." } } }

        if (!root.TryGetProperty("header", out var header))
        {
            _logger.LogDebug("Lark callback missing 'header' field, skipping");
            return null;
        }

        // Verify token on v2 event callbacks (header.token) to reject forged payloads.
        if (!string.IsNullOrWhiteSpace(registration.VerificationToken))
        {
            var headerToken = header.TryGetProperty("token", out var ht) ? ht.GetString() : null;
            if (!string.Equals(headerToken, registration.VerificationToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("Lark event callback token mismatch — ignoring");
                return null;
            }
        }

        var eventType = header.TryGetProperty("event_type", out var et) ? et.GetString() : null;
        if (eventType != "im.message.receive_v1")
        {
            _logger.LogDebug("Lark event type {EventType} is not a message receive, skipping", eventType);
            return null;
        }

        if (!root.TryGetProperty("event", out var eventObj))
            return null;

        // Extract sender
        var senderId = string.Empty;
        var senderName = string.Empty;
        if (eventObj.TryGetProperty("sender", out var sender))
        {
            if (sender.TryGetProperty("sender_id", out var senderIdObj) &&
                senderIdObj.TryGetProperty("open_id", out var openId))
                senderId = openId.GetString() ?? string.Empty;

            if (sender.TryGetProperty("sender_type", out var senderType) &&
                senderType.GetString() == "bot")
            {
                _logger.LogDebug("Ignoring message from bot sender");
                return null;
            }
        }

        // Extract message
        if (!eventObj.TryGetProperty("message", out var message))
            return null;

        var chatId = message.TryGetProperty("chat_id", out var cid) ? cid.GetString() : null;
        var messageId = message.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
        var messageType = message.TryGetProperty("message_type", out var mt) ? mt.GetString() : null;
        var chatType = message.TryGetProperty("chat_type", out var ct) ? ct.GetString() : null;

        if (chatId is null)
        {
            _logger.LogWarning("Lark message missing chat_id");
            return null;
        }

        // Parse message content — Lark wraps text in JSON: {"text":"actual message"}
        string? text = null;
        if (messageType == "text" && message.TryGetProperty("content", out var content))
        {
            var contentStr = content.GetString();
            if (contentStr is not null)
            {
                try
                {
                    using var contentDoc = JsonDocument.Parse(contentStr);
                    text = contentDoc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : contentStr;
                }
                catch
                {
                    text = contentStr;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Lark message has no text content (type={MessageType})", messageType);
            return null;
        }

        _logger.LogInformation("Lark inbound: chat={ChatId}, sender={SenderId}, type={ChatType}",
            chatId, senderId, chatType);

        return new InboundMessage
        {
            Platform = Platform,
            ConversationId = chatId,
            SenderId = senderId,
            SenderName = senderName,
            Text = text,
            MessageId = messageId,
            ChatType = chatType,
        };
    }

    public async Task<PlatformReplyDeliveryResult> SendReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct)
    {
        // Lark Send Message API: POST /open-apis/im/v1/messages?receive_id_type=chat_id
        var body = JsonSerializer.Serialize(new
        {
            receive_id = inbound.ConversationId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new { text = replyText }),
        });

        var result = await nyxClient.ProxyRequestAsync(
            registration.NyxUserToken,
            registration.NyxProviderSlug,
            "open-apis/im/v1/messages?receive_id_type=chat_id",
            "POST",
            body,
            extraHeaders: null,
            ct);

        // ProxyRequestAsync returns error JSON ({"error": true, ...}) instead of
        // throwing on 4xx/5xx responses. Surface these as exceptions so callers
        // can record and diagnose the failure.
        if (result != null && result.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Lark outbound reply failed: slug={Slug}, result={Result}",
                registration.NyxProviderSlug, result);
            throw new InvalidOperationException($"Lark API error: {result}");
        }

        if (TryBuildLarkErrorDetail(result, out var larkErrorDetail))
        {
            _logger.LogWarning(
                "Lark outbound reply rejected by platform: chat={ChatId}, slug={Slug}, detail={Detail}",
                inbound.ConversationId, registration.NyxProviderSlug, larkErrorDetail);
            return new PlatformReplyDeliveryResult(false, larkErrorDetail);
        }

        var successDetail = BuildLarkSuccessDetail(result);
        _logger.LogInformation(
            "Lark outbound reply sent: chat={ChatId}, slug={Slug}, detail={Detail}",
            inbound.ConversationId, registration.NyxProviderSlug, successDetail);
        return new PlatformReplyDeliveryResult(true, successDetail);
    }

    private static bool TryBuildLarkErrorDetail(string? result, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(result))
        {
            detail = "empty_lark_response";
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var codeProp))
                return false;

            var code = codeProp.ValueKind == JsonValueKind.Number
                ? codeProp.GetInt32()
                : 0;
            if (code == 0)
                return false;

            var msg = root.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : null;
            detail = $"lark_code={code}" +
                     (string.IsNullOrWhiteSpace(msg) ? string.Empty : $" msg={msg}");
            return true;
        }
        catch (JsonException)
        {
            detail = "invalid_lark_response_json";
            return true;
        }
    }

    private static string BuildLarkSuccessDetail(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return "result_length=0";

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            var messageId = root.TryGetProperty("data", out var dataProp) &&
                            dataProp.TryGetProperty("message_id", out var messageIdProp)
                ? messageIdProp.GetString()
                : null;
            return string.IsNullOrWhiteSpace(messageId)
                ? $"result_length={result.Length}"
                : $"message_id={messageId}";
        }
        catch
        {
            return $"result_length={result.Length}";
        }
    }
}
