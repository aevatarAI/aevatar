using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
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

        // Read raw body for potential decryption
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        var bodyBytes = ms.ToArray();
        http.Request.Body.Position = 0;
        var bodyString = Encoding.UTF8.GetString(bodyBytes);

        using var doc = JsonDocument.Parse(bodyString);
        var root = doc.RootElement;
        var encryptKey = await ResolveEncryptKeyAsync(http, registration, http.RequestAborted);

        // Lark sends encrypted payloads when encrypt_key is configured:
        // { "encrypt": "<base64-encrypted-string>" }
        if (root.TryGetProperty("encrypt", out var encryptedProp) &&
            !string.IsNullOrEmpty(encryptKey))
        {
            string decrypted;
            try
            {
                decrypted = DecryptLarkPayload(encryptedProp.GetString()!, encryptKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lark URL verification: encrypted payload decryption failed");
                return Results.Unauthorized();
            }

            using var decryptedDoc = JsonDocument.Parse(decrypted);
            var decryptedRoot = decryptedDoc.RootElement;

            if (decryptedRoot.TryGetProperty("type", out var decType) &&
                decType.GetString() == "url_verification")
            {
                var challenge = decryptedRoot.TryGetProperty("challenge", out var ch) ? ch.GetString() : null;
                _logger.LogInformation("Lark encrypted URL verification challenge accepted");
                return Results.Json(new { challenge });
            }

            return null;
        }

        // Lark URL verification challenge (plaintext)
        if (root.TryGetProperty("type", out var typeProp) &&
            typeProp.GetString() == "url_verification")
        {
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

        // Read raw body for signature verification and potential decryption
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        var bodyBytes = ms.ToArray();
        var bodyString = Encoding.UTF8.GetString(bodyBytes);

        using var doc = JsonDocument.Parse(bodyString);
        var root = doc.RootElement;
        var encryptKey = await ResolveEncryptKeyAsync(http, registration, http.RequestAborted);

        // Handle encrypted event payloads: { "encrypt": "<base64>" }
        JsonDocument? decryptedDoc = null;
        try
        {
            if (root.TryGetProperty("encrypt", out var encryptedProp) &&
                !string.IsNullOrEmpty(encryptKey))
            {
                string decrypted;
                try
                {
                    decrypted = DecryptLarkPayload(encryptedProp.GetString()!, encryptKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lark event callback: encrypted payload decryption failed");
                    return null;
                }

                decryptedDoc = JsonDocument.Parse(decrypted);
                root = decryptedDoc.RootElement;
                bodyString = decrypted; // use decrypted body for any further processing
            }

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

            // Signature verification: SHA256(timestamp + nonce + encrypt_key + body)
            // Only when encrypt_key is configured. Uses the ORIGINAL (pre-decryption) body.
            // SECURITY: When encrypt_key is configured, signature is REQUIRED — missing
            // header means forged request. Without this, an attacker can omit the header
            // and bypass verification entirely.
            if (!string.IsNullOrEmpty(encryptKey))
            {
                if (!http.Request.Headers.TryGetValue("X-Lark-Signature", out var sigHeader) ||
                    string.IsNullOrWhiteSpace(sigHeader))
                {
                    _logger.LogWarning("Lark event callback missing required X-Lark-Signature header");
                    return null;
                }

                var timestamp = http.Request.Headers.TryGetValue("X-Lark-Request-Timestamp", out var tsHeader)
                    ? tsHeader.ToString() : "";
                var nonce = http.Request.Headers.TryGetValue("X-Lark-Request-Nonce", out var nonceHeader)
                    ? nonceHeader.ToString() : "";
                var expectedSignature = ComputeLarkSignature(timestamp, nonce, encryptKey,
                    Encoding.UTF8.GetString(bodyBytes)); // always use original body for signature

                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(expectedSignature),
                        Encoding.UTF8.GetBytes(sigHeader.ToString())))
                {
                    _logger.LogWarning("Lark event callback signature verification failed");
                    return null;
                }

                _logger.LogDebug("Lark event callback signature verified");
            }

            // Verify token on v2 event callbacks (header.token) — fallback when no encrypt_key.
            if (string.IsNullOrEmpty(encryptKey) &&
                !string.IsNullOrWhiteSpace(registration.VerificationToken))
            {
                var headerToken = header.TryGetProperty("token", out var ht) ? ht.GetString() : null;
                if (!string.Equals(headerToken, registration.VerificationToken, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Lark event callback token mismatch — ignoring");
                    return null;
                }
            }

            var eventType = header.TryGetProperty("event_type", out var et) ? et.GetString() : null;
            if (eventType == "card.action.trigger")
                return ParseCardAction(root, header);

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
        finally
        {
            decryptedDoc?.Dispose();
        }
    }

    public async Task<PlatformReplyDeliveryResult> SendReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct)
    {
        var isInteractive = IsInteractiveCardPayload(replyText);

        // Lark Send Message API: POST /open-apis/im/v1/messages?receive_id_type=chat_id
        var body = JsonSerializer.Serialize(new
        {
            receive_id = inbound.ConversationId,
            msg_type = isInteractive ? "interactive" : "text",
            content = isInteractive
                ? replyText
                : JsonSerializer.Serialize(new { text = replyText }),
        });

        var result = await nyxClient.ProxyRequestAsync(
            registration.NyxUserToken,
            registration.NyxProviderSlug,
            "open-apis/im/v1/messages?receive_id_type=chat_id",
            "POST",
            body,
            extraHeaders: null,
            ct);

        if (TryBuildLarkFailureResult(result, out var failure))
        {
            _logger.LogWarning(
                "Lark outbound reply rejected: chat={ChatId}, slug={Slug}, detail={Detail}, kind={Kind}",
                inbound.ConversationId, registration.NyxProviderSlug, failure.Detail, failure.FailureKind);
            return failure;
        }

        var successDetail = BuildLarkSuccessDetail(result);
        _logger.LogInformation(
            "Lark outbound reply sent: chat={ChatId}, slug={Slug}, detail={Detail}",
            inbound.ConversationId, registration.NyxProviderSlug, successDetail);
        return new PlatformReplyDeliveryResult(true, successDetail);
    }

    private InboundMessage? ParseCardAction(JsonElement root, JsonElement header)
    {
        if (!root.TryGetProperty("event", out var eventObj))
            return null;

        var senderId =
            TryGetNestedString(eventObj, "operator", "open_id") ??
            TryGetNestedString(eventObj, "operator", "operator_id", "open_id") ??
            string.Empty;
        var chatId =
            TryGetNestedString(eventObj, "context", "open_chat_id") ??
            TryGetNestedString(eventObj, "context", "chat_id");
        var messageId =
            TryGetString(header, "event_id") ??
            TryGetNestedString(eventObj, "context", "open_message_id") ??
            TryGetNestedString(eventObj, "context", "message_id");

        if (string.IsNullOrWhiteSpace(chatId))
        {
            _logger.LogWarning("Lark card action missing open_chat_id");
            return null;
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (eventObj.TryGetProperty("action", out var action))
        {
            CopyScalarValues(action, extra);

            if (action.TryGetProperty("value", out var value))
                CopyScalarValues(value, extra);

            if (action.TryGetProperty("form_value", out var formValue))
                CopyScalarValues(formValue, extra);
        }

        if (!string.IsNullOrWhiteSpace(messageId))
            extra["event_id"] = messageId;

        _logger.LogInformation("Lark card action inbound: chat={ChatId}, sender={SenderId}", chatId, senderId);

        return new InboundMessage
        {
            Platform = Platform,
            ConversationId = chatId,
            SenderId = senderId,
            SenderName = string.Empty,
            Text = eventObj.GetRawText(),
            MessageId = messageId,
            ChatType = "card_action",
            Extra = extra,
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryGetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static void CopyScalarValues(JsonElement element, IDictionary<string, string> target)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    target[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                    target[property.Name] = property.Value.GetRawText();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    target[property.Name] = property.Value.GetBoolean().ToString();
                    break;
            }
        }
    }

    private static async Task<string> ResolveEncryptKeyAsync(
        HttpContext http,
        ChannelBotRegistrationEntry registration,
        CancellationToken ct)
    {
        var credentialRef = registration.CredentialRef?.Trim() ?? string.Empty;
        if (credentialRef.Length == 0)
            return registration.EncryptKey ?? string.Empty;

        var credentialProvider = http.RequestServices.GetService(typeof(ICredentialProvider)) as ICredentialProvider
            ?? throw new InvalidOperationException(
                $"No {nameof(ICredentialProvider)} is registered, but channel registration requires credential_ref '{credentialRef}'.");

        var resolvedSecret = await credentialProvider.ResolveAsync(credentialRef, ct);
        if (string.IsNullOrWhiteSpace(resolvedSecret))
            throw new InvalidOperationException(
                $"credential_ref '{credentialRef}' did not resolve to a Lark credential.");

        return TryExtractEncryptKey(resolvedSecret);
    }

    private static string TryExtractEncryptKey(string resolvedSecret)
    {
        var trimmed = resolvedSecret.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (trimmed[0] != '{')
            return trimmed;

        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.TryGetProperty("encrypt_key", out var encryptKey)
            ? encryptKey.GetString() ?? string.Empty
            : string.Empty;
    }

    internal static bool IsInteractiveCardPayload(string replyText)
    {
        if (string.IsNullOrWhiteSpace(replyText))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(replyText);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = doc.RootElement;
            return root.TryGetProperty("header", out _) ||
                   root.TryGetProperty("elements", out _) ||
                   root.TryGetProperty("i18n_elements", out _) ||
                   root.TryGetProperty("config", out _) ||
                   root.TryGetProperty("card", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryBuildLarkFailureResult(string? result, out PlatformReplyDeliveryResult failure)
    {
        failure = default;
        if (string.IsNullOrWhiteSpace(result))
        {
            failure = new PlatformReplyDeliveryResult(false, "empty_lark_response");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.True)
            {
                var status = root.TryGetProperty("status", out var statusProp) &&
                             statusProp.ValueKind == JsonValueKind.Number
                    ? statusProp.GetInt32()
                    : (int?)null;
                var body = TryReadStringProperty(root, "body");
                var message = TryReadStringProperty(root, "message");

                if (TryBuildLarkPlatformErrorDetail(body, status, out var detail, out var failureKind))
                {
                    failure = new PlatformReplyDeliveryResult(false, detail, failureKind);
                    return true;
                }

                var nyxDetail = $"nyx_error status={status?.ToString() ?? "unknown"}" +
                                (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={body}") +
                                (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}");
                failure = new PlatformReplyDeliveryResult(
                    false,
                    nyxDetail,
                    ClassifyNyxProxyFailure(status, message));
                return true;
            }

            if (TryBuildLarkPlatformErrorDetail(result, status: null, out var larkDetail, out var larkFailureKind))
            {
                failure = new PlatformReplyDeliveryResult(false, larkDetail, larkFailureKind);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            failure = new PlatformReplyDeliveryResult(false, "invalid_lark_response_json");
            return true;
        }
    }

    private static bool TryBuildLarkPlatformErrorDetail(
        string? body,
        int? status,
        out string detail,
        out PlatformReplyFailureKind failureKind)
    {
        detail = string.Empty;
        failureKind = PlatformReplyFailureKind.None;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeProp))
            {
                var code = codeProp.ValueKind == JsonValueKind.Number
                    ? codeProp.GetInt32()
                    : 0;
                if (code == 0)
                    return false;

                var msg = TryReadStringProperty(root, "msg");
                detail = $"lark_code={code}" +
                         (string.IsNullOrWhiteSpace(msg) ? string.Empty : $" msg={msg}");
                failureKind = ClassifyLarkPlatformError(code, error: null, status);
                return true;
            }

            var error = TryReadStringProperty(root, "error");
            var errorCode = root.TryGetProperty("error_code", out var errorCodeProp) &&
                            errorCodeProp.ValueKind == JsonValueKind.Number
                ? errorCodeProp.GetInt32()
                : 0;
            var message = TryReadStringProperty(root, "message");
            if (string.IsNullOrWhiteSpace(error) && errorCode == 0 && string.IsNullOrWhiteSpace(message))
                return false;

            detail = $"nyx_status={status?.ToString() ?? "unknown"}" +
                     (string.IsNullOrWhiteSpace(error) ? string.Empty : $" lark_error={error}") +
                     (errorCode == 0 ? string.Empty : $" error_code={errorCode}") +
                     (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}");
            failureKind = ClassifyLarkPlatformError(errorCode, error, status);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Avoid JsonElement.GetString() on non-string values (e.g. boolean `error:true`),
    // which throws InvalidOperationException and escapes the structured-failure flow.
    private static string? TryReadStringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static PlatformReplyFailureKind ClassifyNyxProxyStatus(int? status) =>
        status switch
        {
            400 or 401 or 403 or 404 => PlatformReplyFailureKind.Permanent,
            408 or 425 or 429 => PlatformReplyFailureKind.Transient,
            >= 500 => PlatformReplyFailureKind.Transient,
            _ => PlatformReplyFailureKind.None,
        };

    private static PlatformReplyFailureKind ClassifyNyxProxyFailure(int? status, string? message)
    {
        if (status.HasValue)
            return ClassifyNyxProxyStatus(status);

        // NyxIdApiClient exception envelopes omit status/body and only preserve the exception message.
        // Treat them as transient so diagnostics retain useful context without promoting them to permanent.
        return string.IsNullOrWhiteSpace(message)
            ? PlatformReplyFailureKind.None
            : PlatformReplyFailureKind.Transient;
    }

    private static PlatformReplyFailureKind ClassifyLarkPlatformError(int errorCode, string? error, int? status)
    {
        // 230001 means the receive target no longer exists or is invalid; 230002 means the
        // configured bot/user is not in the target chat. Both require operator/configuration fixes.
        if (errorCode is 230001 or 230002)
            return PlatformReplyFailureKind.Permanent;

        if (string.Equals(error, "token_expired", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(error, "invalid_auth", StringComparison.OrdinalIgnoreCase))
            return PlatformReplyFailureKind.Permanent;

        return ClassifyNyxProxyStatus(status);
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

    // ─── Lark Crypto Helpers ───

    /// <summary>
    /// Compute Lark webhook signature: SHA256(timestamp + nonce + encrypt_key + body).
    /// Lark's v2 event subscription protocol uses this for webhook payload integrity.
    /// </summary>
    internal static string ComputeLarkSignature(string timestamp, string nonce, string encryptKey, string body)
    {
        var message = timestamp + nonce + encryptKey + body;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Decrypt Lark encrypted event payload.
    /// Lark uses AES-256-CBC: key = SHA256(encrypt_key), IV = first 16 bytes of ciphertext.
    /// </summary>
    internal static string DecryptLarkPayload(string encrypted, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var ciphertext = Convert.FromBase64String(encrypted);

        if (ciphertext.Length < 16)
            throw new CryptographicException("Lark encrypted payload too short for IV extraction");

        var iv = ciphertext[..16];
        var content = ciphertext[16..];

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(content, 0, content.Length);
        return Encoding.UTF8.GetString(decrypted);
    }
}
