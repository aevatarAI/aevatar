using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using ICredentialProvider = Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider;

namespace Aevatar.GAgents.Channel.Telegram;

/// <summary>
/// Full Telegram channel adapter backed by the official <c>Telegram.Bot</c> SDK.
/// </summary>
public sealed class TelegramChannelAdapter : IChannelTransport, IChannelOutboundPort
{
    private readonly Channel<ChatActivity> _inbound = System.Threading.Channels.Channel.CreateBounded<ChatActivity>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly ICredentialProvider _credentialProvider;
    private readonly ITelegramApiClient _apiClient;
    private readonly ITelegramAttachmentContentResolver? _attachmentResolver;
    private readonly TelegramMessageComposer _composer;
    private readonly ILogger<TelegramChannelAdapter> _logger;
    private readonly TelegramChannelAdapterOptions _options;
    private ChannelTransportBinding? _binding;
    private bool _initialized;
    private bool _receiving;
    private bool _stopped;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoop;

    /// <summary>
    /// Creates one Telegram channel adapter.
    /// </summary>
    public TelegramChannelAdapter(
        ICredentialProvider credentialProvider,
        ITelegramApiClient apiClient,
        ITelegramAttachmentContentResolver? attachmentResolver = null,
        TelegramMessageComposer? composer = null,
        ILogger<TelegramChannelAdapter>? logger = null,
        TelegramChannelAdapterOptions? options = null)
    {
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _attachmentResolver = attachmentResolver;
        _composer = composer ?? new TelegramMessageComposer();
        _logger = logger ?? NullLogger<TelegramChannelAdapter>.Instance;
        _options = options ?? new TelegramChannelAdapterOptions();
        Capabilities = new ChannelCapabilities
        {
            SupportsEphemeral = false,
            SupportsEdit = true,
            SupportsDelete = true,
            SupportsThread = false,
            Streaming = StreamingSupport.EditLoopRateLimited,
            SupportsFiles = true,
            MaxMessageLength = 4096,
            SupportsActionButtons = true,
            SupportsConfirmDialog = false,
            SupportsModal = false,
            SupportsMention = false,
            SupportsTyping = false,
            SupportsReactions = false,
            RecommendedStreamDebounceMs = _options.RecommendedStreamDebounceMs,
            Transport = _options.TransportMode,
        };
    }

    /// <inheritdoc />
    public ChannelId Channel { get; } = ChannelId.From("telegram");

    /// <inheritdoc />
    public TransportMode TransportMode => _options.TransportMode;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; }

    /// <inheritdoc />
    public ChannelReader<ChatActivity> InboundStream => _inbound.Reader;

    /// <summary>
    /// Accepts one webhook payload, normalizes it into <see cref="ChatActivity"/>, and enqueues it on <see cref="InboundStream"/>.
    /// </summary>
    public async Task<ChatActivity?> AcceptWebhookAsync(
        Stream payload,
        string? secretToken = null,
        ChannelTransportBinding? binding = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var activeBinding = binding ?? _binding ?? throw new InvalidOperationException("Adapter has not been initialized.");
        if (!ValidateWebhookSecret(secretToken, activeBinding))
            return null;

        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: ct);
        var update = document.Deserialize<Update>(JsonBotAPI.Options);
        if (update is null)
            return null;

        return await ProcessUpdateAsync(update, activeBinding, enqueue: true, ct);
    }

    /// <summary>
    /// Starts one streaming reply against the supplied Telegram conversation reference.
    /// </summary>
    public async Task<StreamingHandle> BeginStreamingReplyAsync(
        ConversationReference reference,
        MessageContent initial,
        CancellationToken ct)
    {
        var initialEmit = await SendAsync(reference, initial, ct);
        if (!initialEmit.Success)
            throw new InvalidOperationException($"Unable to start Telegram streaming reply: {initialEmit.ErrorCode}");

        return new TelegramStreamingHandle(
            this,
            reference,
            initialEmit.SentActivityId,
            initial.Text,
            _options.TimeProvider,
            TimeSpan.FromMilliseconds(Math.Max(_options.RecommendedStreamDebounceMs, 0)));
    }

    /// <inheritdoc />
    public Task InitializeAsync(ChannelTransportBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_initialized)
            throw new InvalidOperationException("Adapter has already been initialized.");
        if (_receiving)
            throw new InvalidOperationException("Adapter cannot initialize after receive startup.");

        _binding = binding.Clone();
        _initialized = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartReceivingAsync(CancellationToken ct)
    {
        EnsureInitialized();
        if (_receiving)
            throw new InvalidOperationException("Adapter has already started receiving.");

        _receiving = true;
        if (_options.TransportMode == TransportMode.LongPolling)
        {
            _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoop = Task.Run(() => RunLongPollingLoopAsync(_receiveLoopCts.Token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
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

        _inbound.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct)
    {
        try
        {
            EnsureReady();
            var target = TelegramConversationTarget.Parse(to);
            var context = new ComposeContext
            {
                Conversation = to.Clone(),
                Capabilities = Capabilities,
            };
            var payload = _composer.Compose(content, context);
            if (payload.Attachment is null && string.IsNullOrWhiteSpace(payload.Text))
            {
                return EmitResult.Failed(
                    "telegram_empty_message",
                    "Telegram text sends require non-empty message content.",
                    capability: payload.Capability);
            }

            var botToken = await ResolveBotTokenAsync(ct);

            TelegramSentActivity sent;
            if (payload.Attachment is null)
            {
                sent = await _apiClient.SendMessageAsync(
                    botToken,
                    target.ChatId,
                    payload.Text,
                    payload.ReplyMarkup,
                    replyToMessageId: null,
                    ct);
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
                        ? await _apiClient.SendPhotoAsync(
                            botToken,
                            target.ChatId,
                            attachment,
                            payload.Text,
                            payload.ReplyMarkup,
                            replyToMessageId: null,
                            ct)
                        : await _apiClient.SendDocumentAsync(
                            botToken,
                            target.ChatId,
                            attachment,
                            payload.Text,
                            payload.ReplyMarkup,
                            replyToMessageId: null,
                            ct);
                }
                finally
                {
                    attachment.Content?.Dispose();
                }
            }

            return EmitResult.Sent(TelegramConversationTarget.BuildActivityId(sent.ChatId, sent.MessageId), payload.Capability);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return EmitResult.Failed("credential_resolution_failed", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram send failed.");
            return EmitResult.Failed("telegram_send_failed", SanitizeError(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct)
    {
        try
        {
            EnsureReady();
            var target = TelegramConversationTarget.Parse(to);
            var adapterActivity = TelegramConversationTarget.ParseActivityId(activityId);
            var payload = _composer.Compose(content, new ComposeContext
            {
                Conversation = to.Clone(),
                Capabilities = Capabilities,
            });
            if (string.IsNullOrWhiteSpace(payload.Text))
            {
                return EmitResult.Failed(
                    "telegram_empty_message",
                    "Telegram text updates require non-empty message content.",
                    capability: payload.Capability);
            }

            if (payload.Attachment is not null)
            {
                return EmitResult.Failed(
                    "telegram_edit_text_only",
                    "Telegram edits are limited to text payloads in this adapter.",
                    capability: ComposeCapability.Degraded);
            }

            var botToken = await ResolveBotTokenAsync(ct);
            await _apiClient.EditMessageTextAsync(
                botToken,
                target.ChatId,
                adapterActivity.MessageId,
                payload.Text,
                payload.ReplyMarkup,
                ct);
            return EmitResult.Sent(TelegramConversationTarget.BuildActivityId(target.ChatId, adapterActivity.MessageId), payload.Capability);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram update failed.");
            return EmitResult.Failed("telegram_update_failed", SanitizeError(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct)
    {
        EnsureReady();
        var target = TelegramConversationTarget.Parse(to);
        var adapterActivity = TelegramConversationTarget.ParseActivityId(activityId);
        var botToken = await ResolveBotTokenAsync(ct);
        await _apiClient.DeleteMessageAsync(botToken, target.ChatId, adapterActivity.MessageId, ct);
    }

    /// <inheritdoc />
    public async Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        Aevatar.GAgents.Channel.Abstractions.AuthContext auth,
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

        return await SendAsync(reference, content, ct);
    }

    private async Task RunLongPollingLoopAsync(CancellationToken ct)
    {
        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            string botToken;
            try
            {
                botToken = await ResolveBotTokenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Telegram long polling stopped because bot credentials could not be resolved.");
                break;
            }

            try
            {
                var updates = await _apiClient.GetUpdatesAsync(
                    botToken,
                    offset <= 0 ? null : offset,
                    _options.LongPollingTimeoutSeconds,
                    ct);

                foreach (var update in updates.OrderBy(static update => update.Id))
                {
                    offset = Math.Max(offset, update.Id + 1);
                    if (_binding is null)
                        continue;
                    await ProcessUpdateAsync(update, _binding, enqueue: true, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ApiRequestException ex) when (IsTerminalAuthFailure(ex))
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

    private async Task<ChatActivity?> ProcessUpdateAsync(
        Update update,
        ChannelTransportBinding binding,
        bool enqueue,
        CancellationToken ct)
    {
        var activity = NormalizeUpdate(update, binding);
        if (activity is null)
            return null;

        if (enqueue && _receiving)
            await _inbound.Writer.WriteAsync(activity, ct);

        return activity;
    }

    private ChatActivity? NormalizeUpdate(Update update, ChannelTransportBinding binding)
    {
        if (TryNormalizeMessage(update.Id, update.Message, binding, out var messageActivity))
            return messageActivity;
        if (TryNormalizeMessage(update.Id, update.ChannelPost, binding, out var channelActivity))
            return channelActivity;
        if (TryNormalizeMessage(update.Id, update.EditedMessage, binding, out var editedActivity))
            return editedActivity;
        if (TryNormalizeMessage(update.Id, update.EditedChannelPost, binding, out var editedChannelActivity))
            return editedChannelActivity;
        if (TryNormalizeCallback(update.Id, update.CallbackQuery, binding, out var callbackActivity))
            return callbackActivity;

        return null;
    }

    private bool TryNormalizeMessage(
        int updateId,
        Message? message,
        ChannelTransportBinding binding,
        out ChatActivity? activity)
    {
        activity = null;
        if (message is null)
            return false;
        if (message.From?.IsBot == true)
            return false;

        var text = !string.IsNullOrWhiteSpace(message.Text)
            ? message.Text
            : message.Caption;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var conversation = CreateConversation(binding.Bot.Bot, message.Chat);
        var senderId = message.From is not null
            ? message.From.Id.ToString()
            : message.SenderChat?.Id.ToString() ?? message.Chat.Id.ToString();
        var senderName = message.From?.Username
                         ?? message.From?.FirstName
                         ?? message.SenderChat?.Title
                         ?? message.Chat.Title
                         ?? senderId;
        activity = new ChatActivity
        {
            Id = $"telegram:update:{updateId}",
            Type = ActivityType.Message,
            ChannelId = Channel.Clone(),
            Bot = binding.Bot.Bot.Clone(),
            Conversation = conversation,
            From = new ParticipantRef
            {
                CanonicalId = senderId,
                DisplayName = senderName,
            },
            Timestamp = Timestamp.FromDateTime(message.Date.ToUniversalTime()),
            Content = new MessageContent
            {
                Text = text,
                Disposition = MessageDisposition.Normal,
            },
            ReplyToActivityId = message.ReplyToMessage is not null
                ? TelegramConversationTarget.BuildActivityId(message.Chat.Id, message.ReplyToMessage.MessageId)
                : string.Empty,
            RawPayloadBlobRef = string.Empty,
        };
        return true;
    }

    private bool TryNormalizeCallback(
        int updateId,
        CallbackQuery? callback,
        ChannelTransportBinding binding,
        out ChatActivity? activity)
    {
        activity = null;
        if (callback?.Message is null)
            return false;

        var data = string.IsNullOrWhiteSpace(callback.Data) ? callback.GameShortName : callback.Data;
        if (string.IsNullOrWhiteSpace(data))
            return false;

        var conversation = CreateConversation(binding.Bot.Bot, callback.Message.Chat);
        activity = new ChatActivity
        {
            Id = $"telegram:update:{updateId}",
            Type = ActivityType.CardAction,
            ChannelId = Channel.Clone(),
            Bot = binding.Bot.Bot.Clone(),
            Conversation = conversation,
            From = new ParticipantRef
            {
                CanonicalId = callback.From.Id.ToString(),
                DisplayName = callback.From.Username ?? callback.From.FirstName ?? callback.From.Id.ToString(),
            },
            Timestamp = Timestamp.FromDateTime(callback.Message.Date.ToUniversalTime()),
            Content = new MessageContent
            {
                Text = data,
                Disposition = MessageDisposition.Normal,
            },
            ReplyToActivityId = TelegramConversationTarget.BuildActivityId(callback.Message.Chat.Id, callback.Message.MessageId),
            RawPayloadBlobRef = string.Empty,
        };
        return true;
    }

    private static ConversationReference CreateConversation(BotInstanceId bot, Chat chat) =>
        chat.Type switch
        {
            ChatType.Private => ConversationReference.TelegramPrivate(bot, chat.Id.ToString()),
            ChatType.Group => ConversationReference.TelegramGroup(bot, chat.Id.ToString()),
            ChatType.Supergroup => ConversationReference.TelegramGroup(bot, chat.Id.ToString(), isSupergroup: true),
            ChatType.Channel => ConversationReference.TelegramChannel(bot, chat.Id.ToString()),
            _ => ConversationReference.TelegramPrivate(bot, chat.Id.ToString()),
        };

    private bool ValidateWebhookSecret(string? secretToken, ChannelTransportBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.VerificationToken))
            return true;

        if (string.IsNullOrEmpty(secretToken))
        {
            _logger.LogWarning("Telegram webhook secret token mismatch.");
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(binding.VerificationToken);
        var actual = Encoding.UTF8.GetBytes(secretToken);
        if (expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual))
            return true;

        _logger.LogWarning("Telegram webhook secret token mismatch.");
        return false;
    }

    private async Task<string> ResolveBotTokenAsync(CancellationToken ct)
    {
        if (_binding is null)
            throw new InvalidOperationException("Adapter binding is unavailable.");

        return await _credentialProvider.ResolveBotCredentialAsync(_binding, ct)
            ?? throw new InvalidOperationException("Telegram bot credential could not be resolved.");
    }

    private async Task<TelegramAttachmentContent?> ResolveAttachmentAsync(AttachmentRef attachment, CancellationToken ct)
    {
        if (_attachmentResolver is null)
            return null;

        return await _attachmentResolver.ResolveAsync(attachment, ct);
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _binding is null)
            throw new InvalidOperationException("Adapter must be initialized before startup.");
    }

    private void EnsureReady()
    {
        EnsureInitialized();
        if (!_receiving)
            throw new InvalidOperationException("Adapter must start receiving before outbound operations.");
    }

    private static string SanitizeError(string message) =>
        string.IsNullOrWhiteSpace(message) ? "telegram_error" : message.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();

    private static bool IsTerminalAuthFailure(ApiRequestException ex) => ex.ErrorCode is 401 or 403;

    private readonly record struct TelegramConversationTarget(long ChatId, int MessageId)
    {
        public static TelegramConversationTarget Parse(ConversationReference reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (!string.Equals(reference.Channel.Value, "telegram", StringComparison.Ordinal))
                throw new InvalidOperationException("Conversation reference does not target Telegram.");

            var segments = reference.CanonicalKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 3 || !long.TryParse(segments[^1], out var chatId))
                throw new InvalidOperationException("Telegram canonical key must end with the numeric chat id.");
            return new TelegramConversationTarget(chatId, 0);
        }

        public static TelegramConversationTarget ParseActivityId(string activityId)
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

        public static string BuildActivityId(long chatId, int messageId) => $"telegram:message:{chatId}:{messageId}";
    }

    private sealed class TelegramStreamingHandle : StreamingHandle
    {
        private readonly TelegramChannelAdapter _adapter;
        private readonly ConversationReference _reference;
        private readonly string _activityId;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _debounce;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly HashSet<long> _acceptedSequenceNumbers = [];
        private readonly Dictionary<long, string> _chunks = new();
        private string _currentText;
        private bool _completed;
        private CancellationTokenSource? _flushCts;
        private long _flushGeneration;

        public TelegramStreamingHandle(
            TelegramChannelAdapter adapter,
            ConversationReference reference,
            string activityId,
            string currentText,
            TimeProvider timeProvider,
            TimeSpan debounce)
        {
            _adapter = adapter;
            _reference = reference;
            _activityId = activityId;
            _currentText = currentText;
            _timeProvider = timeProvider;
            _debounce = debounce;
        }

        public override async Task AppendAsync(StreamChunk chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);

            CancellationTokenSource? previousFlush = null;
            await _writeGate.WaitAsync(CancellationToken.None);
            try
            {
                if (_completed || !_acceptedSequenceNumbers.Add(chunk.SequenceNumber))
                    return;

                _chunks[chunk.SequenceNumber] = chunk.Delta;
                previousFlush = _flushCts;
                _flushCts = new CancellationTokenSource();
                _ = FlushLaterAsync(++_flushGeneration, _flushCts.Token);
            }
            finally
            {
                _writeGate.Release();
            }

            CancelPendingFlush(previousFlush);
        }

        public override async Task CompleteAsync(MessageContent final)
        {
            ArgumentNullException.ThrowIfNull(final);

            Task? finalWrite = null;
            await _writeGate.WaitAsync(CancellationToken.None);
            try
            {
                if (_completed)
                    return;

                _completed = true;
                CancelPendingFlush(_flushCts);
                _flushCts = null;
                finalWrite = _adapter.UpdateAsync(_reference, _activityId, final, CancellationToken.None);
                await finalWrite;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            Task? interruptedWrite = null;
            await _writeGate.WaitAsync(CancellationToken.None);
            try
            {
                if (_completed)
                    return;

                _completed = true;
                CancelPendingFlush(_flushCts);
                _flushCts = null;
                var interrupted = new MessageContent
                {
                    Text = string.IsNullOrWhiteSpace(_currentText) ? "(interrupted)" : $"{_currentText}\n\n(interrupted)",
                    Disposition = MessageDisposition.Normal,
                };
                interruptedWrite = _adapter.UpdateAsync(_reference, _activityId, interrupted, CancellationToken.None);
                await interruptedWrite;
            }
            catch
            {
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task FlushLaterAsync(long generation, CancellationToken ct)
        {
            try
            {
                if (_debounce > TimeSpan.Zero)
                    await Task.Delay(_debounce, _timeProvider, ct);

                await _writeGate.WaitAsync(ct);
                try
                {
                    if (_completed || ct.IsCancellationRequested || generation != _flushGeneration || _chunks.Count == 0)
                        return;

                    _currentText += string.Concat(_chunks.OrderBy(static pair => pair.Key).Select(static pair => pair.Value));
                    _chunks.Clear();
                    await _adapter.UpdateAsync(_reference, _activityId, new MessageContent
                    {
                        Text = _currentText,
                        Disposition = MessageDisposition.Normal,
                    }, ct);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static void CancelPendingFlush(CancellationTokenSource? flushCts)
        {
            if (flushCts is null)
                return;

            flushCts.Cancel();
            flushCts.Dispose();
        }
    }
}
