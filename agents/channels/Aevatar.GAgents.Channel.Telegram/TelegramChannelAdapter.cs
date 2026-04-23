using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using FoundationCredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.Channel.Telegram;

public sealed class TelegramChannelAdapter : IChannelTransport, IChannelOutboundPort
{
    private static readonly ChannelId TelegramChannel = ChannelId.From("telegram");

    private readonly HttpClient _httpClient;
    private readonly FoundationCredentialProvider _credentialProvider;
    private readonly IMessageComposer<TelegramOutboundMessage> _composer;
    private readonly IPayloadRedactor _payloadRedactor;
    private readonly ILogger<TelegramChannelAdapter> _logger;
    private readonly System.Threading.Channels.Channel<ChatActivity> _inboundBuffer;
    private readonly ChannelCapabilities _capabilities;
    private readonly ITelegramAttachmentContentResolver? _attachmentResolver;
    private readonly TelegramChannelAdapterOptions _options;
    private readonly bool _captureInboundActivities;

    private ChannelTransportBinding? _binding;
    private TelegramCredentialSnapshot _botCredential = new(string.Empty);
    private bool _initialized;
    private bool _receiving;
    private bool _stopped;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoop;

    public TelegramChannelAdapter(
        FoundationCredentialProvider credentialProvider,
        TelegramMessageComposer composer,
        IPayloadRedactor payloadRedactor,
        ILogger<TelegramChannelAdapter> logger,
        HttpClient? httpClient = null,
        ITelegramAttachmentContentResolver? attachmentResolver = null,
        TelegramChannelAdapterOptions? options = null,
        bool captureInboundActivities = true)
    {
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _payloadRedactor = payloadRedactor ?? throw new ArgumentNullException(nameof(payloadRedactor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _attachmentResolver = attachmentResolver;
        _options = options ?? new TelegramChannelAdapterOptions();
        _captureInboundActivities = captureInboundActivities;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = TelegramChannelDefaults.DefaultBaseAddress,
        };
        _capabilities = TelegramMessageComposer.DefaultCapabilities.Clone();
        _capabilities.RecommendedStreamDebounceMs = _options.RecommendedStreamDebounceMs;
        _capabilities.Transport = _options.TransportMode;
        _inboundBuffer = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(
            new System.Threading.Channels.BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });
    }

    public ChannelId Channel => TelegramChannel;

    public TransportMode TransportMode => _options.TransportMode;

    public ChannelCapabilities Capabilities => _capabilities.Clone();

    public System.Threading.Channels.ChannelReader<ChatActivity> InboundStream => _inboundBuffer.Reader;

    public async Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (_initialized || _receiving)
            throw new InvalidOperationException("TelegramChannelAdapter is already initialized.");

        var secret = await _credentialProvider.ResolveBotCredentialAsync(binding, ct);
        _binding = binding.Clone();
        _botCredential = TelegramCredentialSnapshot.Parse(secret);
        _initialized = true;
        _stopped = false;
    }

    public Task StartReceivingAsync(CancellationToken ct)
    {
        EnsureInitialized();
        if (_receiving)
            throw new InvalidOperationException("TelegramChannelAdapter has already started receiving.");

        _receiving = true;
        if (_options.TransportMode == TransportMode.LongPolling)
        {
            _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoop = Task.Run(() => RunLongPollingLoopAsync(_receiveLoopCts.Token));
        }

        return Task.CompletedTask;
    }

    public async Task StopReceivingAsync(CancellationToken ct)
    {
        if (_stopped)
            return;

        _stopped = true;
        _receiving = false;
        if (_receiveLoopCts is not null)
        {
            await _receiveLoopCts.CancelAsync();
            _receiveLoopCts.Dispose();
            _receiveLoopCts = null;
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }

            _receiveLoop = null;
        }

        _inboundBuffer.Writer.TryComplete();
    }

    public async Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct) =>
        await SendCoreAsync(to, content, activityId: null, await RefreshBotCredentialAsync(ct), ct);

    public async Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct) =>
        await SendCoreAsync(to, content, activityId, await RefreshBotCredentialAsync(ct), ct);

    public async Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(to);
        if (string.IsNullOrWhiteSpace(activityId))
            throw new ArgumentException("Activity id cannot be empty.", nameof(activityId));

        var target = TelegramConversationTarget.ParseConversation(to);
        var outboundActivity = TelegramConversationTarget.ParseOutboundActivityId(activityId);
        if (target.ChatId != outboundActivity.ChatId)
            throw new InvalidOperationException("Telegram activity id does not belong to the supplied conversation.");

        var credential = await RefreshBotCredentialAsync(ct);
        if (string.IsNullOrWhiteSpace(credential.BotToken))
            throw new InvalidOperationException("Telegram bot credential could not be resolved.");

        await DeleteMessageAsync(credential.BotToken, outboundActivity.ChatId, outboundActivity.MessageId, ct);
    }

    public async Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        AuthContext auth,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(auth);

        if (auth.Kind == PrincipalKind.OnBehalfOfUser)
        {
            return EmitResult.Failed(
                "principal_unsupported",
                "Telegram Bot API does not support delegated user sends.",
                capability: ComposeCapability.Unsupported);
        }

        return await SendCoreAsync(reference, content, activityId: null, await RefreshBotCredentialAsync(ct), ct);
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

        return new TelegramStreamingHandle(
            this,
            to.Clone(),
            sent.SentActivityId,
            initial.Clone(),
            _options.TimeProvider,
            TimeSpan.FromMilliseconds(Math.Max(_options.RecommendedStreamDebounceMs, 0)));
    }

    public async Task<TelegramWebhookResponse> HandleWebhookAsync(TelegramWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureReady();

        if (!ValidateWebhookSecret(request.Headers))
            return new TelegramWebhookResponse(401, null, null, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.Body ?? Array.Empty<byte>());
        }
        catch (JsonException)
        {
            return new TelegramWebhookResponse(400, null, null, null);
        }

        using (document)
        {
            var activity = NormalizeUpdate(document.RootElement, _binding!);
            if (activity is null)
                return new TelegramWebhookResponse(200, null, null, null);

            byte[] sanitizedPayload;
            try
            {
                sanitizedPayload = (await _payloadRedactor.RedactAsync(Channel, request.Body ?? Array.Empty<byte>(), ct)).SanitizedPayload;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram payload redaction failed closed.");
                return new TelegramWebhookResponse(503, null, null, null);
            }

            activity.RawPayloadBlobRef = BuildBlobRef(sanitizedPayload);
            if (_captureInboundActivities)
                await _inboundBuffer.Writer.WriteAsync(activity, ct);

            return new TelegramWebhookResponse(200, null, activity, sanitizedPayload);
        }
    }

    private async Task<EmitResult> SendCoreAsync(
        ConversationReference to,
        MessageContent content,
        string? activityId,
        TelegramCredentialSnapshot credential,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(content);
        EnsureReady();

        var capability = _composer.Evaluate(content, ComposeContextFor(to));
        if (capability == ComposeCapability.Unsupported)
            return EmitResult.Failed("unsupported_content", "Telegram composer rejected the message.", capability: capability);

        if (string.IsNullOrWhiteSpace(credential.BotToken))
            return EmitResult.Failed("credential_resolution_failed", "Telegram bot credential could not be resolved.", capability: capability);

        try
        {
            var effectiveContent = content.Clone();
            if (effectiveContent.Disposition == MessageDisposition.Ephemeral)
                effectiveContent.Disposition = MessageDisposition.Normal;

            var payload = _composer.Compose(effectiveContent, ComposeContextFor(to));
            TelegramConversationTarget target;
            try
            {
                target = TelegramConversationTarget.ParseConversation(to);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Telegram conversation reference is invalid.");
                return EmitResult.Failed("telegram_invalid_conversation", ex.Message, capability: capability);
            }

            var reportedCapability = capability == ComposeCapability.Exact ? payload.Capability : capability;

            if (activityId is null)
            {
                if (payload.Attachment is null && string.IsNullOrWhiteSpace(payload.Text))
                {
                    return EmitResult.Failed(
                        "telegram_empty_message",
                        "Telegram text sends require non-empty message content.",
                        capability: payload.Capability);
                }

                TelegramSentActivity sent;
                if (payload.Attachment is null)
                {
                    sent = await SendMessageAsync(credential.BotToken, target.ChatId, payload.Text, payload.ReplyMarkupJson, ct);
                }
                else
                {
                    var attachment = await ResolveAttachmentAsync(payload.Attachment, ct);
                    if (attachment is null)
                    {
                        return EmitResult.Failed(
                            "attachment_unavailable",
                            "Telegram attachment content could not be resolved.",
                            capability: payload.Capability);
                    }

                    try
                    {
                        sent = payload.Attachment.Kind == AttachmentKind.Image
                            ? await SendMediaAsync("sendPhoto", "photo", credential.BotToken, target.ChatId, attachment, payload.Text, payload.ReplyMarkupJson, ct)
                            : await SendMediaAsync("sendDocument", "document", credential.BotToken, target.ChatId, attachment, payload.Text, payload.ReplyMarkupJson, ct);
                    }
                    finally
                    {
                        attachment.Content?.Dispose();
                    }
                }

                return EmitResult.Sent(
                    TelegramConversationTarget.BuildOutboundActivityId(sent.ChatId, sent.MessageId),
                    reportedCapability);
            }

            if (string.IsNullOrWhiteSpace(payload.Text))
            {
                return EmitResult.Failed(
                    "telegram_empty_message",
                    "Telegram text updates require non-empty message content.",
                    capability: reportedCapability);
            }

            if (payload.Attachment is not null)
            {
                return EmitResult.Failed(
                    "telegram_edit_text_only",
                    "Telegram edits are limited to text payloads in this adapter.",
                    capability: ComposeCapability.Degraded);
            }

            var outboundActivity = TelegramConversationTarget.ParseOutboundActivityId(activityId);
            if (target.ChatId != outboundActivity.ChatId)
            {
                return EmitResult.Failed(
                    "telegram_activity_mismatch",
                    "Telegram activity id does not belong to the supplied conversation.",
                    capability: payload.Capability);
            }

            await EditMessageTextAsync(
                credential.BotToken,
                outboundActivity.ChatId,
                outboundActivity.MessageId,
                payload.Text,
                payload.ReplyMarkupJson,
                ct);

            return EmitResult.Sent(
                TelegramConversationTarget.BuildOutboundActivityId(outboundActivity.ChatId, outboundActivity.MessageId),
                reportedCapability);
        }
        catch (TelegramApiException ex)
        {
            _logger.LogWarning(ex, "Telegram request failed.");
            return EmitResult.Failed(ex.FailureCode, ex.Message, capability: capability);
        }
    }

    private async Task RunLongPollingLoopAsync(CancellationToken ct)
    {
        long? offset = null;
        while (!ct.IsCancellationRequested)
        {
            TelegramCredentialSnapshot credential;
            try
            {
                credential = await RefreshBotCredentialAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(credential.BotToken))
            {
                _logger.LogError("Telegram long polling stopped because bot credentials could not be resolved.");
                break;
            }

            try
            {
                var updates = await FetchUpdatesAsync(credential.BotToken, offset, ct);
                foreach (var update in updates.OrderBy(GetUpdateId))
                {
                    var updateId = GetUpdateId(update);
                    if (updateId.HasValue)
                        offset = updateId.Value + 1;

                    var activity = NormalizeUpdate(update, _binding!);
                    if (activity is null || !_captureInboundActivities)
                        continue;

                    await _inboundBuffer.Writer.WriteAsync(activity, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramApiException ex) when (IsTerminalAuthFailure(ex))
            {
                _logger.LogError(ex, "Telegram long polling stopped because bot authorization failed.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram long polling iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), _options.TimeProvider, ct);
            }
        }
    }

    private ChatActivity? NormalizeUpdate(JsonElement update, ChannelTransportBinding binding)
    {
        var updateId = GetUpdateId(update);
        if (update.TryGetProperty("message", out var message))
            return ParseMessage(updateId, message, binding);
        if (update.TryGetProperty("edited_message", out var editedMessage))
            return ParseMessage(updateId, editedMessage, binding);
        if (update.TryGetProperty("channel_post", out var channelPost))
            return ParseMessage(updateId, channelPost, binding);
        if (update.TryGetProperty("edited_channel_post", out var editedChannelPost))
            return ParseMessage(updateId, editedChannelPost, binding);
        if (update.TryGetProperty("callback_query", out var callback))
            return ParseCallback(updateId, callback, binding);

        return null;
    }

    private ChatActivity? ParseMessage(long? updateId, JsonElement message, ChannelTransportBinding binding)
    {
        if (TryReadNestedBoolean(message, "from", "is_bot") == true)
            return null;

        var chatId = TryReadNestedInt64(message, "chat", "id");
        if (!chatId.HasValue)
            return null;

        var text = TryReadString(message, "text") ?? TryReadString(message, "caption") ?? string.Empty;
        var attachments = ExtractAttachments(message);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            return null;

        var senderId = TryReadNestedInt64(message, "from", "id")?.ToString(CultureInfo.InvariantCulture)
            ?? TryReadNestedInt64(message, "sender_chat", "id")?.ToString(CultureInfo.InvariantCulture)
            ?? chatId.Value.ToString(CultureInfo.InvariantCulture);
        var senderName = TryReadNestedString(message, "from", "username")
            ?? TryReadNestedString(message, "from", "first_name")
            ?? TryReadNestedString(message, "sender_chat", "title")
            ?? TryReadNestedString(message, "chat", "title")
            ?? senderId;

        var content = new MessageContent
        {
            Text = text,
            Disposition = MessageDisposition.Normal,
        };
        content.Attachments.AddRange(attachments);

        return new ChatActivity
        {
            Id = ChatActivity.BuildActivityId(Channel, BuildDeliveryKey(updateId, $"message:{chatId}:{TryReadInt32(message, "message_id")}")),
            Type = ActivityType.Message,
            ChannelId = Channel.Clone(),
            Bot = binding.Bot.Bot.Clone(),
            Conversation = CreateConversation(binding.Bot.Bot, message),
            From = new ParticipantRef
            {
                CanonicalId = senderId,
                DisplayName = senderName,
            },
            Timestamp = Timestamp.FromDateTimeOffset(ParseTimestamp(message)),
            Content = content,
            ReplyToActivityId = BuildReplyToActivityId(message, chatId.Value),
        };
    }

    private ChatActivity? ParseCallback(long? updateId, JsonElement callback, ChannelTransportBinding binding)
    {
        if (!callback.TryGetProperty("message", out var message))
            return null;

        var chatId = TryReadNestedInt64(message, "chat", "id");
        var messageId = TryReadInt32(message, "message_id");
        if (!chatId.HasValue || !messageId.HasValue)
            return null;

        var data = TryReadString(callback, "data") ?? TryReadString(callback, "game_short_name");
        if (string.IsNullOrWhiteSpace(data))
            return null;

        var sourceMessageId = TelegramConversationTarget.BuildOutboundActivityId(chatId.Value, messageId.Value);
        return new ChatActivity
        {
            Id = ChatActivity.BuildActivityId(Channel, BuildDeliveryKey(updateId, $"callback:{TryReadString(callback, "id") ?? sourceMessageId}")),
            Type = ActivityType.CardAction,
            ChannelId = Channel.Clone(),
            Bot = binding.Bot.Bot.Clone(),
            Conversation = CreateConversation(binding.Bot.Bot, message),
            From = new ParticipantRef
            {
                CanonicalId = TryReadNestedInt64(callback, "from", "id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                DisplayName = TryReadNestedString(callback, "from", "username")
                    ?? TryReadNestedString(callback, "from", "first_name")
                    ?? TryReadNestedInt64(callback, "from", "id")?.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty,
            },
            Timestamp = Timestamp.FromDateTimeOffset(ParseTimestamp(message)),
            Content = new MessageContent
            {
                Disposition = MessageDisposition.Normal,
                CardAction = new CardActionSubmission
                {
                    ActionId = data,
                    SubmittedValue = data,
                    SourceMessageId = sourceMessageId,
                },
            },
            ReplyToActivityId = sourceMessageId,
        };
    }

    private static List<AttachmentRef> ExtractAttachments(JsonElement message)
    {
        var attachments = new List<AttachmentRef>();

        if (message.TryGetProperty("photo", out var photos) && photos.ValueKind == JsonValueKind.Array)
        {
            var lastPhoto = photos.EnumerateArray().LastOrDefault();
            if (lastPhoto.ValueKind != JsonValueKind.Undefined && TryReadString(lastPhoto, "file_id") is { Length: > 0 } photoId)
            {
                attachments.Add(new AttachmentRef
                {
                    AttachmentId = photoId,
                    Kind = AttachmentKind.Image,
                    Name = "photo.jpg",
                    ContentType = "image/jpeg",
                    SizeBytes = TryReadInt64(lastPhoto, "file_size") ?? 0,
                });
            }
        }

        if (message.TryGetProperty("document", out var document) && TryReadString(document, "file_id") is { Length: > 0 } documentId)
        {
            attachments.Add(new AttachmentRef
            {
                AttachmentId = documentId,
                Kind = AttachmentKind.File,
                Name = TryReadString(document, "file_name") ?? "document.bin",
                ContentType = TryReadString(document, "mime_type") ?? "application/octet-stream",
                SizeBytes = TryReadInt64(document, "file_size") ?? 0,
            });
        }

        if (message.TryGetProperty("audio", out var audio) && TryReadString(audio, "file_id") is { Length: > 0 } audioId)
        {
            attachments.Add(new AttachmentRef
            {
                AttachmentId = audioId,
                Kind = AttachmentKind.Audio,
                Name = TryReadString(audio, "file_name") ?? "audio.bin",
                ContentType = TryReadString(audio, "mime_type") ?? "audio/mpeg",
                SizeBytes = TryReadInt64(audio, "file_size") ?? 0,
            });
        }

        if (message.TryGetProperty("video", out var video) && TryReadString(video, "file_id") is { Length: > 0 } videoId)
        {
            attachments.Add(new AttachmentRef
            {
                AttachmentId = videoId,
                Kind = AttachmentKind.Video,
                Name = TryReadString(video, "file_name") ?? "video.mp4",
                ContentType = TryReadString(video, "mime_type") ?? "video/mp4",
                SizeBytes = TryReadInt64(video, "file_size") ?? 0,
            });
        }

        if (message.TryGetProperty("voice", out var voice) && TryReadString(voice, "file_id") is { Length: > 0 } voiceId)
        {
            attachments.Add(new AttachmentRef
            {
                AttachmentId = voiceId,
                Kind = AttachmentKind.Audio,
                Name = "voice.ogg",
                ContentType = TryReadString(voice, "mime_type") ?? "audio/ogg",
                SizeBytes = TryReadInt64(voice, "file_size") ?? 0,
            });
        }

        return attachments;
    }

    private static ConversationReference CreateConversation(BotInstanceId bot, JsonElement message)
    {
        var chatType = TryReadNestedString(message, "chat", "type");
        var chatId = TryReadNestedInt64(message, "chat", "id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        return chatType switch
        {
            "group" => ConversationReference.TelegramGroup(bot, chatId),
            "supergroup" => ConversationReference.TelegramGroup(bot, chatId, isSupergroup: true),
            "channel" => ConversationReference.TelegramChannel(bot, chatId),
            _ => ConversationReference.TelegramPrivate(bot, chatId),
        };
    }

    private bool ValidateWebhookSecret(IReadOnlyDictionary<string, string>? headers)
    {
        if (_binding is null || string.IsNullOrWhiteSpace(_binding.VerificationToken))
            return true;

        if (!TryGetHeader(headers, TelegramChannelDefaults.SecretHeaderName, out var providedSecret) ||
            string.IsNullOrWhiteSpace(providedSecret))
        {
            _logger.LogWarning("Telegram webhook secret token mismatch.");
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(_binding.VerificationToken));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedSecret.Trim()));
        if (CryptographicOperations.FixedTimeEquals(expectedHash, providedHash))
            return true;

        _logger.LogWarning("Telegram webhook secret token mismatch.");
        return false;
    }

    private async Task<TelegramCredentialSnapshot> RefreshBotCredentialAsync(CancellationToken ct)
    {
        EnsureInitialized();
        var secret = await _credentialProvider.ResolveBotCredentialAsync(_binding!, ct);
        var refreshed = TelegramCredentialSnapshot.Parse(secret);
        if (string.IsNullOrWhiteSpace(refreshed.BotToken))
            return _botCredential;

        _botCredential = refreshed;
        return refreshed;
    }

    private async Task<TelegramAttachmentContent?> ResolveAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        if (_attachmentResolver is null)
            return null;

        return await _attachmentResolver.ResolveAsync(attachment, ct);
    }

    private async Task<IReadOnlyList<JsonElement>> FetchUpdatesAsync(
        string botToken,
        long? offset,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["timeout"] = _options.LongPollingTimeoutSeconds,
        };
        if (offset.HasValue)
            body["offset"] = offset.Value;

        body["allowed_updates"] = new JsonArray(TelegramChannelDefaults.AllowedUpdateTypes.Select(static updateType => (JsonNode?)updateType).ToArray());

        var response = await SendTelegramRequestAsync(botToken, "getUpdates", BuildJsonContent(body), ct);
        if (!response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();

        return result.EnumerateArray().Select(static item => item.Clone()).ToArray();
    }

    private async Task<TelegramSentActivity> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        string? replyMarkupJson,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
        };
        AppendReplyMarkup(body, replyMarkupJson);

        var response = await SendTelegramRequestAsync(botToken, "sendMessage", BuildJsonContent(body), ct);
        return ParseSentActivity(response, chatId);
    }

    private async Task<TelegramSentActivity> SendMediaAsync(
        string methodName,
        string mediaFieldName,
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        string? replyMarkupJson,
        CancellationToken ct)
    {
        HttpContent content;
        if (!string.IsNullOrWhiteSpace(attachment.ExternalUrl) || !string.IsNullOrWhiteSpace(attachment.TelegramFileId))
        {
            var body = new JsonObject
            {
                ["chat_id"] = chatId,
                [mediaFieldName] = !string.IsNullOrWhiteSpace(attachment.TelegramFileId)
                    ? attachment.TelegramFileId
                    : attachment.ExternalUrl,
            };
            if (!string.IsNullOrWhiteSpace(caption))
                body["caption"] = caption;
            AppendReplyMarkup(body, replyMarkupJson);
            content = BuildJsonContent(body);
        }
        else
        {
            if (attachment.Content is null)
                throw new InvalidOperationException("Telegram attachment must provide content, external URL, or file id.");

            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
            if (!string.IsNullOrWhiteSpace(caption))
                multipart.Add(new StringContent(caption), "caption");
            if (!string.IsNullOrWhiteSpace(replyMarkupJson))
                multipart.Add(new StringContent(replyMarkupJson), "reply_markup");

            var fileContent = new StreamContent(attachment.Content);
            if (!string.IsNullOrWhiteSpace(attachment.ContentType))
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(attachment.ContentType);
            multipart.Add(fileContent, mediaFieldName, string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment.bin" : attachment.FileName);
            content = multipart;
        }

        var response = await SendTelegramRequestAsync(botToken, methodName, content, ct);
        return ParseSentActivity(response, chatId);
    }

    private async Task EditMessageTextAsync(
        string botToken,
        long chatId,
        int messageId,
        string text,
        string? replyMarkupJson,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["text"] = text,
        };
        AppendReplyMarkup(body, replyMarkupJson);

        await SendTelegramRequestAsync(botToken, "editMessageText", BuildJsonContent(body), ct);
    }

    private async Task DeleteMessageAsync(
        string botToken,
        long chatId,
        int messageId,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
        };

        await SendTelegramRequestAsync(botToken, "deleteMessage", BuildJsonContent(body), ct);
    }

    private async Task<JsonElement> SendTelegramRequestAsync(
        string botToken,
        string methodName,
        HttpContent content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/bot{botToken}/{methodName}")
        {
            Content = content,
        };
        using var response = await _httpClient.SendAsync(request, ct);
        var responseText = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException((int)response.StatusCode, responseText);

        if (string.IsNullOrWhiteSpace(responseText))
            return default;

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement.Clone();
        if (root.TryGetProperty("ok", out var okProperty) &&
            okProperty.ValueKind == JsonValueKind.False)
        {
            throw CreateApiException(null, responseText);
        }

        return root;
    }

    private ComposeContext ComposeContextFor(ConversationReference to) => new()
    {
        Conversation = to.Clone(),
        Capabilities = Capabilities.Clone(),
    };

    private void EnsureInitialized()
    {
        if (!_initialized || _binding is null)
            throw new InvalidOperationException("TelegramChannelAdapter.InitializeAsync must run first.");
    }

    private void EnsureReady()
    {
        EnsureInitialized();
        if (!_receiving || _stopped)
            throw new InvalidOperationException("TelegramChannelAdapter must be started before use.");
    }

    private static void AppendReplyMarkup(JsonObject body, string? replyMarkupJson)
    {
        if (string.IsNullOrWhiteSpace(replyMarkupJson))
            return;

        body["reply_markup"] = JsonNode.Parse(replyMarkupJson);
    }

    private static HttpContent BuildJsonContent(JsonObject body) =>
        new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static TelegramSentActivity ParseSentActivity(JsonElement response, long fallbackChatId)
    {
        var chatId = TryReadNestedInt64(response, "result", "chat", "id") ?? fallbackChatId;
        var messageId = TryReadNestedInt32(response, "result", "message_id")
            ?? throw new TelegramApiException("telegram_missing_message_id", "Telegram response did not include a message id.", null);
        return new TelegramSentActivity(chatId, messageId);
    }

    private static string BuildDeliveryKey(long? updateId, string fallback) =>
        updateId.HasValue ? $"update:{updateId.Value}" : fallback;

    private static string BuildReplyToActivityId(JsonElement message, long chatId)
    {
        var replyMessageId = TryReadNestedInt32(message, "reply_to_message", "message_id");
        return replyMessageId.HasValue
            ? TelegramConversationTarget.BuildOutboundActivityId(chatId, replyMessageId.Value)
            : string.Empty;
    }

    private static DateTimeOffset ParseTimestamp(JsonElement message)
    {
        var unixSeconds = TryReadInt64(message, "date");
        return unixSeconds.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value)
            : DateTimeOffset.UnixEpoch;
    }

    private static long? GetUpdateId(JsonElement update) => TryReadInt64(update, "update_id");

    private static bool IsTerminalAuthFailure(TelegramApiException ex) => ex.ErrorCode is 401 or 403;

    private static string BuildBlobRef(byte[] sanitizedPayload)
    {
        var hash = SHA256.HashData(sanitizedPayload);
        return $"telegram-raw:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static TelegramApiException CreateApiException(int? statusCode, string body)
    {
        var errorCode = ParsePlatformErrorCode(body) ?? statusCode;
        return new TelegramApiException(
            MapErrorCode(statusCode, body),
            BuildSanitizedError(statusCode, body),
            errorCode);
    }

    private static int? ParsePlatformErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return TryReadInt32(document.RootElement, "error_code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MapErrorCode(int? statusCode, string body)
    {
        var platformCode = ParsePlatformErrorCode(body);
        if (platformCode.HasValue)
            return $"telegram_{platformCode.Value}";

        return statusCode.HasValue
            ? $"http_{statusCode.Value}"
            : "telegram_request_failed";
    }

    private static string BuildSanitizedError(int? statusCode, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var description = TryReadString(document.RootElement, "description") ?? TryReadString(document.RootElement, "message");
                if (!string.IsNullOrWhiteSpace(description))
                    return statusCode.HasValue ? $"{statusCode.Value} {description}" : description;
            }
            catch (JsonException)
            {
            }
        }

        return statusCode.HasValue
            ? $"{statusCode.Value} telegram request failed"
            : "telegram request failed";
    }

    private static bool TryGetHeader(IReadOnlyDictionary<string, string>? headers, string headerName, out string value)
    {
        value = string.Empty;
        if (headers is null)
            return false;

        if (headers.TryGetValue(headerName, out value!))
            return true;

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        return false;
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? TryReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : null;

    private static bool? TryReadNestedBoolean(JsonElement element, params string[] segments)
    {
        var current = element;
        foreach (var segment in segments)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

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

    private static long? TryReadNestedInt64(JsonElement element, params string[] segments)
    {
        var current = element;
        foreach (var segment in segments)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static int? TryReadNestedInt32(JsonElement element, params string[] segments)
    {
        var current = element;
        foreach (var segment in segments)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value)
            ? value
            : null;
    }

    private readonly record struct TelegramConversationTarget(long ChatId, int MessageId)
    {
        public static TelegramConversationTarget ParseConversation(ConversationReference reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (!string.Equals(reference.Channel.Value, TelegramChannel.Value, StringComparison.Ordinal))
                throw new InvalidOperationException("Conversation reference does not target Telegram.");

            var segments = reference.CanonicalKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 3 || !long.TryParse(segments[^1], out var chatId))
                throw new InvalidOperationException("Telegram canonical key must end with the numeric chat id.");

            return new TelegramConversationTarget(chatId, 0);
        }

        public static TelegramConversationTarget ParseOutboundActivityId(string activityId)
        {
            ArgumentNullException.ThrowIfNull(activityId);
            var parts = activityId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                string.Equals(parts[0], "telegram", StringComparison.Ordinal) &&
                string.Equals(parts[1], "message", StringComparison.Ordinal) &&
                long.TryParse(parts[2], out var chatId) &&
                int.TryParse(parts[3], out var messageId))
            {
                return new TelegramConversationTarget(chatId, messageId);
            }

            throw new InvalidOperationException("Telegram activity id is invalid.");
        }

        public static string BuildOutboundActivityId(long chatId, int messageId) => $"telegram:message:{chatId}:{messageId}";
    }

    private readonly record struct TelegramSentActivity(long ChatId, int MessageId);

    private sealed class TelegramApiException : Exception
    {
        public TelegramApiException(string failureCode, string message, int? errorCode)
            : base(message)
        {
            FailureCode = failureCode;
            ErrorCode = errorCode;
        }

        public string FailureCode { get; }

        public int? ErrorCode { get; }
    }
}
