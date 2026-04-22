using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.Channel.Lark;

public sealed class LarkChannelAdapter : IChannelTransport, IChannelOutboundPort
{
    private static readonly Regex MentionRegex = new(
        "<at\\s+user_id=\\\"(?<id>[^\\\"]+)\\\">(?<label>.*?)</at>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly ChannelId LarkChannel = ChannelId.From("lark");
    private static readonly TimeSpan SignatureValidityWindow = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly FoundationCredentialProvider _credentialProvider;
    private readonly IMessageComposer<LarkOutboundMessage> _composer;
    private readonly IPayloadRedactor _payloadRedactor;
    private readonly ILogger<LarkChannelAdapter> _logger;
    private readonly System.Threading.Channels.Channel<ChatActivity> _inboundBuffer;
    private readonly ChannelCapabilities _capabilities;
    private readonly bool _captureInboundActivities;

    private ChannelTransportBinding? _binding;
    private LarkCredentialSnapshot _botCredential = new(string.Empty, string.Empty);
    private bool _initialized;
    private bool _receiving;
    private bool _stopped;

    public LarkChannelAdapter(
        FoundationCredentialProvider credentialProvider,
        LarkMessageComposer composer,
        IPayloadRedactor payloadRedactor,
        ILogger<LarkChannelAdapter> logger,
        HttpClient? httpClient = null,
        bool captureInboundActivities = true)
    {
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _payloadRedactor = payloadRedactor ?? throw new ArgumentNullException(nameof(payloadRedactor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _captureInboundActivities = captureInboundActivities;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = LarkChannelDefaults.DefaultBaseAddress,
        };
        _capabilities = LarkMessageComposer.DefaultCapabilities.Clone();
        _inboundBuffer = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(new System.Threading.Channels.BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
        });
    }

    public ChannelId Channel => LarkChannel;

    public TransportMode TransportMode => TransportMode.Webhook;

    public ChannelCapabilities Capabilities => _capabilities.Clone();

    public System.Threading.Channels.ChannelReader<ChatActivity> InboundStream => _inboundBuffer.Reader;

    public async Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_initialized || _receiving)
            throw new InvalidOperationException("LarkChannelAdapter is already initialized.");

        var secret = await _credentialProvider.ResolveBotCredentialAsync(binding, ct);
        _binding = binding.Clone();
        _botCredential = LarkCredentialSnapshot.Parse(secret);
        _initialized = true;
        _stopped = false;
    }

    public Task StartReceivingAsync(CancellationToken ct)
    {
        EnsureInitialized();
        if (_receiving)
            throw new InvalidOperationException("LarkChannelAdapter has already started receiving.");

        _receiving = true;
        return Task.CompletedTask;
    }

    public Task StopReceivingAsync(CancellationToken ct)
    {
        if (_stopped)
            return Task.CompletedTask;

        _stopped = true;
        _receiving = false;
        _inboundBuffer.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct) =>
        SendCoreAsync(to, content, _botCredential, activityId: null, HttpMethod.Post, ct, ComposeContextFor(to));

    public Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct) =>
        SendCoreAsync(to, content, _botCredential, activityId, HttpMethod.Put, ct, ComposeContextFor(to));

    public async Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(to);
        if (string.IsNullOrWhiteSpace(activityId))
            throw new ArgumentException("Activity id cannot be empty.", nameof(activityId));

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/open-apis/im/v1/messages/{Uri.EscapeDataString(activityId.Trim())}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botCredential.AccessToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(BuildSanitizedError(response.StatusCode, body));
    }

    public async Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        AuthContext auth,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auth);
        EnsureReady();

        var credential = auth.Kind == PrincipalKind.OnBehalfOfUser
            ? LarkCredentialSnapshot.Parse(await _credentialProvider.ResolveUserCredentialAsync(auth, ct))
            : _botCredential;

        if (string.IsNullOrWhiteSpace(credential.AccessToken))
            return EmitResult.Failed("credential_resolution_failed", "Resolved credential is empty.");

        return await SendCoreAsync(reference, content, credential, activityId: null, HttpMethod.Post, ct, ComposeContextFor(reference));
    }

    public async Task<StreamingHandle> BeginStreamingReplyAsync(
        ConversationReference to,
        MessageContent initial,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(initial);

        var sent = await SendAsync(to, initial, ct);
        if (!sent.Success)
            throw new InvalidOperationException(sent.ErrorMessage);

        return new LarkStreamingHandle(this, to.Clone(), sent.SentActivityId, initial.Clone());
    }

    public async Task<LarkWebhookResponse> HandleWebhookAsync(LarkWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureReady();

        var rawBytes = request.Body ?? Array.Empty<byte>();
        var rawText = Encoding.UTF8.GetString(rawBytes);

        JsonDocument bodyDocument;
        try
        {
            bodyDocument = JsonDocument.Parse(rawText);
        }
        catch (JsonException)
        {
            return new LarkWebhookResponse(StatusCode: 400, ResponseBody: null, Activity: null, SanitizedPayload: null);
        }

        using (bodyDocument)
        {
            var root = bodyDocument.RootElement;
            var encrypted = TryReadString(root, "encrypt");
            var decryptedText = rawText;
            var isUrlVerification = TryReadString(root, "type") == "url_verification";
            if (!string.IsNullOrWhiteSpace(_botCredential.EncryptKey) &&
                string.IsNullOrWhiteSpace(encrypted) &&
                !VerifySignature(request.Headers, _botCredential.EncryptKey, rawText))
            {
                return new LarkWebhookResponse(StatusCode: 401, ResponseBody: null, Activity: null, SanitizedPayload: null);
            }

            if (!string.IsNullOrWhiteSpace(encrypted) && !string.IsNullOrWhiteSpace(_botCredential.EncryptKey))
            {
                if (!VerifySignature(request.Headers, _botCredential.EncryptKey, rawText))
                    return new LarkWebhookResponse(StatusCode: 401, ResponseBody: null, Activity: null, SanitizedPayload: null);

                decryptedText = DecryptLarkPayload(encrypted, _botCredential.EncryptKey);
                using var decrypted = JsonDocument.Parse(decryptedText);
                return await HandleParsedWebhookAsync(decrypted.RootElement, decryptedText, request.Headers, ct);
            }

            return await HandleParsedWebhookAsync(root, decryptedText, request.Headers, ct);
        }
    }

    internal static string ComputeLarkSignature(string timestamp, string nonce, string encryptKey, string body)
    {
        var raw = string.Concat(timestamp ?? string.Empty, nonce ?? string.Empty, encryptKey ?? string.Empty, body ?? string.Empty);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string DecryptLarkPayload(string encrypted, string encryptKey)
    {
        var cipher = Convert.FromBase64String(encrypted);
        if (cipher.Length < 17)
            throw new CryptographicException("Lark encrypted payload too short for IV extraction");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = cipher[..16];
        var body = cipher[16..];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(body, 0, body.Length);
        return Encoding.UTF8.GetString(plaintext);
    }

    private async Task<LarkWebhookResponse> HandleParsedWebhookAsync(
        JsonElement root,
        string effectiveBody,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        if (TryReadString(root, "type") == "url_verification")
        {
            if (!TokenMatches(root, "token"))
                return new LarkWebhookResponse(StatusCode: 401, ResponseBody: null, Activity: null, SanitizedPayload: null);

            var challenge = TryReadString(root, "challenge") ?? string.Empty;
            return new LarkWebhookResponse(200, JsonSerializer.Serialize(new { challenge }), null, null);
        }

        if (!root.TryGetProperty("header", out var header))
            return new LarkWebhookResponse(200, null, null, null);

        if (string.IsNullOrWhiteSpace(_botCredential.EncryptKey) && !TokenMatches(header, "token"))
            return new LarkWebhookResponse(StatusCode: 401, ResponseBody: null, Activity: null, SanitizedPayload: null);

        var eventType = TryReadString(header, "event_type");
        ChatActivity? activity = eventType switch
        {
            "im.message.receive_v1" => ParseMessage(root),
            "card.action.trigger" => ParseCardAction(root, header),
            _ => null,
        };

        if (activity is null)
            return new LarkWebhookResponse(200, null, null, null);

        byte[] sanitizedPayload;
        try
        {
            sanitizedPayload = (await _payloadRedactor.RedactAsync(Channel, Encoding.UTF8.GetBytes(effectiveBody), ct)).SanitizedPayload;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lark payload redaction failed closed.");
            return new LarkWebhookResponse(StatusCode: 503, ResponseBody: null, Activity: null, SanitizedPayload: null);
        }

        activity.RawPayloadBlobRef = BuildBlobRef(sanitizedPayload);
        if (_captureInboundActivities)
            await _inboundBuffer.Writer.WriteAsync(activity, ct);
        return new LarkWebhookResponse(200, null, activity, sanitizedPayload);
    }

    private ChatActivity? ParseMessage(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventObject) ||
            !eventObject.TryGetProperty("message", out var message))
            return null;

        var sender = eventObject.TryGetProperty("sender", out var senderElement) ? senderElement : default;
        var senderId = TryReadNestedString(sender, "sender_id", "open_id") ?? string.Empty;
        var senderType = TryReadString(sender, "sender_type") ?? string.Empty;
        if (string.Equals(senderType, "bot", StringComparison.OrdinalIgnoreCase))
            return null;

        var chatId = TryReadString(message, "chat_id");
        if (string.IsNullOrWhiteSpace(chatId))
            return null;

        var messageId = TryReadString(message, "message_id") ?? Guid.NewGuid().ToString("N");
        var chatType = TryReadString(message, "chat_type") ?? string.Empty;
        var text = ExtractTextContent(message);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var mentions = ParseMentions(text, _binding!.Bot.Bot.Value, out var normalizedText);
        var conversation = BuildConversation(chatType, senderId, chatId);
        var activity = new ChatActivity
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = Channel.Clone(),
            Bot = _binding!.Bot.Bot.Clone(),
            Conversation = conversation,
            From = new ParticipantRef
            {
                CanonicalId = senderId,
                DisplayName = TryReadString(sender, "sender_type") == "user"
                    ? (TryReadNestedString(sender, "sender_id", "open_id") ?? senderId)
                    : senderId,
            },
            Content = new MessageContent
            {
                Text = normalizedText,
                Disposition = MessageDisposition.Normal,
            },
            Timestamp = Timestamp.FromDateTimeOffset(ParseTimestamp(message)),
        };

        activity.Mentions.AddRange(mentions);
        return activity;
    }

    private ChatActivity? ParseCardAction(JsonElement root, JsonElement header)
    {
        if (!root.TryGetProperty("event", out var eventObject))
            return null;

        var senderId = TryReadNestedString(eventObject, "operator", "open_id")
                       ?? TryReadNestedString(eventObject, "operator", "operator_id", "open_id")
                       ?? string.Empty;
        var chatId = TryReadNestedString(eventObject, "context", "open_chat_id")
                     ?? TryReadNestedString(eventObject, "context", "chat_id");
        if (string.IsNullOrWhiteSpace(chatId))
            return null;

        var conversation = TryBuildCardActionConversation(eventObject, senderId, chatId);
        if (conversation is null)
            return null;

        return new ChatActivity
        {
            Id = TryReadString(header, "event_id")
                 ?? TryReadNestedString(eventObject, "context", "open_message_id")
                 ?? Guid.NewGuid().ToString("N"),
            Type = ActivityType.CardAction,
            ChannelId = Channel.Clone(),
            Bot = _binding!.Bot.Bot.Clone(),
            Conversation = conversation,
            From = new ParticipantRef
            {
                CanonicalId = senderId,
                DisplayName = senderId,
            },
            Content = new MessageContent
            {
                Disposition = MessageDisposition.Normal,
                CardAction = BuildCardActionSubmission(eventObject),
            },
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private async Task<EmitResult> SendCoreAsync(
        ConversationReference to,
        MessageContent content,
        LarkCredentialSnapshot credential,
        string? activityId,
        HttpMethod method,
        CancellationToken ct,
        ComposeContext composeContext)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(content);
        EnsureReady();

        if (string.IsNullOrWhiteSpace(credential.AccessToken))
            return EmitResult.Failed("credential_resolution_failed", "Resolved credential is empty.");

        var capability = _composer.Evaluate(content, composeContext);
        if (capability == ComposeCapability.Unsupported)
            return EmitResult.Failed("unsupported_content", "Lark composer rejected the message.", capability: capability);

        var effectiveContent = content.Clone();
        if (effectiveContent.Disposition == MessageDisposition.Ephemeral)
            effectiveContent.Disposition = MessageDisposition.Normal;

        var payload = _composer.Compose(effectiveContent, composeContext);
        var target = ResolveTarget(to);
        using var request = BuildRequest(method, target.ReceiveIdType, target.ReceiveId, activityId, payload, credential.AccessToken);
        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return EmitResult.Failed(MapErrorCode(response.StatusCode, body), BuildSanitizedError(response.StatusCode, body), capability: capability);

        var sentActivityId = activityId;
        if (string.IsNullOrWhiteSpace(sentActivityId))
            sentActivityId = ParseMessageId(body) ?? Guid.NewGuid().ToString("N");

        return EmitResult.Sent(sentActivityId!, capability);
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        string receiveIdType,
        string receiveId,
        string? activityId,
        LarkOutboundMessage payload,
        string accessToken)
    {
        var path = method == HttpMethod.Post
            ? $"/open-apis/im/v1/messages?receive_id_type={Uri.EscapeDataString(receiveIdType)}"
            : $"/open-apis/im/v1/messages/{Uri.EscapeDataString(activityId!)}";
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (method != HttpMethod.Delete)
        {
            var body = method == HttpMethod.Post
                ? JsonSerializer.Serialize(new
                {
                    receive_id = receiveId,
                    msg_type = payload.MessageType,
                    content = payload.ContentJson,
                })
                : JsonSerializer.Serialize(new
                {
                    msg_type = payload.MessageType,
                    content = payload.ContentJson,
                });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private ComposeContext ComposeContextFor(ConversationReference to) => new()
    {
        Conversation = to.Clone(),
        Capabilities = Capabilities.Clone(),
    };

    private (string ReceiveIdType, string ReceiveId) ResolveTarget(ConversationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.Scope == ConversationScope.Group || reference.Scope == ConversationScope.Thread)
        {
            return ("chat_id", LastCanonicalSegment(reference.CanonicalKey));
        }

        return ("open_id", LastCanonicalSegment(reference.CanonicalKey));
    }

    private static string LastCanonicalSegment(string canonicalKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalKey))
            throw new ArgumentException("Conversation canonical key cannot be empty.", nameof(canonicalKey));

        var parts = canonicalKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new ArgumentException("Conversation canonical key must contain a target segment.", nameof(canonicalKey));

        return parts[^1];
    }

    private ConversationReference BuildConversation(string chatType, string senderId, string chatId) =>
        string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase)
            ? ConversationReference.Create(Channel, _binding!.Bot.Bot, ConversationScope.DirectMessage, chatId, "dm", senderId)
            : ConversationReference.Create(Channel, _binding!.Bot.Bot, ConversationScope.Group, chatId, "group", chatId);

    private ConversationReference? TryBuildCardActionConversation(JsonElement eventObject, string senderId, string chatId)
    {
        var chatType = TryReadNestedString(eventObject, "context", "chat_type")
                       ?? TryReadNestedString(eventObject, "context", "open_chat_type")
                       ?? TryReadNestedString(eventObject, "context", "conversation_type");
        if (string.IsNullOrWhiteSpace(chatType))
        {
            _logger.LogWarning("Lark card action missing chat_type for chat {ChatId}; dropping callback.", chatId);
            return null;
        }

        if (string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(senderId))
            {
                _logger.LogWarning("Lark card action missing sender id for p2p chat {ChatId}; dropping callback.", chatId);
                return null;
            }

            return ConversationReference.Create(Channel, _binding!.Bot.Bot, ConversationScope.DirectMessage, chatId, "dm", senderId);
        }

        if (string.Equals(chatType, "group", StringComparison.OrdinalIgnoreCase))
            return ConversationReference.Create(Channel, _binding!.Bot.Bot, ConversationScope.Group, chatId, "group", chatId);

        _logger.LogWarning("Lark card action chat_type {ChatType} is unsupported for chat {ChatId}; dropping callback.", chatType, chatId);
        return null;
    }

    private static CardActionSubmission BuildCardActionSubmission(JsonElement eventObject)
    {
        var submission = new CardActionSubmission
        {
            SourceMessageId = TryReadNestedString(eventObject, "context", "open_message_id")
                              ?? TryReadNestedString(eventObject, "context", "message_id")
                              ?? string.Empty,
        };

        if (!eventObject.TryGetProperty("action", out var action))
            return submission;

        submission.ActionId =
            TryReadNestedString(action, "value", "action_id") ??
            TryReadString(action, "action_id") ??
            string.Empty;

        if (action.TryGetProperty("value", out var value))
        {
            submission.SubmittedValue = TryReadString(value, "value") ?? TryReadScalar(value) ?? string.Empty;
            CopyScalarValues(value, submission.Arguments, excludedKeys: ["action_id", "value"]);
        }

        if (action.TryGetProperty("form_value", out var formValue))
            CopyScalarValues(formValue, submission.FormFields);

        return submission;
    }

    private static List<ParticipantRef> ParseMentions(string text, string botId, out string normalizedText)
    {
        var mentions = new List<ParticipantRef>();
        normalizedText = MentionRegex.Replace(text, match =>
        {
            var id = match.Groups["id"].Value;
            var label = string.IsNullOrWhiteSpace(match.Groups["label"].Value) ? id : match.Groups["label"].Value;
            mentions.Add(new ParticipantRef
            {
                CanonicalId = id,
                DisplayName = label,
            });

            return string.Equals(id, botId, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : label;
        });

        normalizedText = Regex.Replace(normalizedText, "\\s{2,}", " ").Trim();
        return mentions;
    }

    private bool TokenMatches(JsonElement element, string propertyName)
    {
        if (_binding is null || string.IsNullOrWhiteSpace(_binding.VerificationToken))
            return false;

        var token = TryReadString(element, propertyName);
        return string.Equals(token, _binding.VerificationToken, StringComparison.Ordinal);
    }

    private static bool VerifySignature(IReadOnlyDictionary<string, string>? headers, string encryptKey, string body)
    {
        if (headers is null)
            return false;

        if (!headers.TryGetValue("X-Lark-Signature", out var signature) || string.IsNullOrWhiteSpace(signature))
            return false;

        if (!headers.TryGetValue("X-Lark-Request-Timestamp", out var timestamp) ||
            !TryParseSignatureTimestamp(timestamp, out var requestTime))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - requestTime;
        if (age.Duration() > SignatureValidityWindow)
            return false;

        headers.TryGetValue("X-Lark-Request-Nonce", out var nonce);
        var expected = ComputeLarkSignature(timestamp ?? string.Empty, nonce ?? string.Empty, encryptKey, body);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    private static bool TryParseSignatureTimestamp(string? timestamp, out DateTimeOffset requestTime)
    {
        requestTime = default;
        if (!long.TryParse(timestamp, out var raw))
            return false;

        requestTime = timestamp!.Length > 10
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw);
        return true;
    }

    private static string? ExtractTextContent(JsonElement message)
    {
        var messageType = TryReadString(message, "message_type");
        if (!string.Equals(messageType, "text", StringComparison.OrdinalIgnoreCase))
            return null;

        var content = TryReadString(message, "content");
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            using var document = JsonDocument.Parse(content);
            return TryReadString(document.RootElement, "text") ?? content;
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static DateTimeOffset ParseTimestamp(JsonElement message)
    {
        var createTime = TryReadString(message, "create_time");
        if (long.TryParse(createTime, out var unixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

        return DateTimeOffset.UtcNow;
    }

    private static string? ParseMessageId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data))
                return TryReadString(data, "message_id");
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? TryReadScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null,
    };

    private static void CopyScalarValues(
        JsonElement element,
        Google.Protobuf.Collections.MapField<string, string> target,
        params string[] excludedKeys)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (excludedKeys.Contains(property.Name, StringComparer.Ordinal))
                continue;

            var scalar = TryReadScalar(property.Value);
            if (scalar is not null)
                target[property.Name] = scalar;
        }
    }

    private static string MapErrorCode(HttpStatusCode statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
                    return $"lark_{code.GetInt32()}";
            }
            catch (JsonException)
            {
            }
        }

        return $"http_{(int)statusCode}";
    }

    private static string BuildSanitizedError(HttpStatusCode statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var message = TryReadString(root, "msg") ?? TryReadString(root, "message");
                if (!string.IsNullOrWhiteSpace(message))
                    return $"{(int)statusCode} {message}";
            }
            catch (JsonException)
            {
            }
        }

        return $"{(int)statusCode} lark request failed";
    }

    private static string BuildBlobRef(byte[] sanitizedPayload)
    {
        var hash = SHA256.HashData(sanitizedPayload);
        return $"lark-raw:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _binding is null)
            throw new InvalidOperationException("LarkChannelAdapter.InitializeAsync must run first.");
    }

    private void EnsureReady()
    {
        EnsureInitialized();
        if (!_receiving || _stopped)
            throw new InvalidOperationException("LarkChannelAdapter must be started before use.");
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryReadNestedString(JsonElement element, params string[] segments)
    {
        var current = element;
        foreach (var segment in segments)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }
}
