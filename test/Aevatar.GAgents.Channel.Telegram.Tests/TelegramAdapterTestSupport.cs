using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using global::Telegram.Bot;
using global::Telegram.Bot.Exceptions;
using global::Telegram.Bot.Types;
using TelegramInlineKeyboardMarkup = global::Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

internal sealed class TelegramAdapterHarness
{
    private TelegramChannelAdapter _adapter;
    private TelegramWebhookFixture _webhook;

    public TelegramAdapterHarness(TransportMode transportMode = TransportMode.Webhook)
    {
        Credentials = new FakeCredentialProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vault://telegram/primary"] = "bot-token-primary",
            ["vault://telegram/secondary"] = "bot-token-secondary",
        });
        Api = new FakeTelegramApiClient();
        Attachments = new FakeTelegramAttachmentContentResolver();
        _adapter = CreateAdapter(transportMode);
        _webhook = new TelegramWebhookFixture(_adapter);
    }

    public FakeCredentialProvider Credentials { get; }

    public FakeTelegramApiClient Api { get; }

    public FakeTelegramAttachmentContentResolver Attachments { get; }

    public ChannelTransportBinding DefaultBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create("telegram-primary", ChannelId.From("telegram"), BotInstanceId.From("telegram-primary-bot")),
        "vault://telegram/primary",
        "secret-primary");

    public ChannelTransportBinding SecondaryBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create("telegram-secondary", ChannelId.From("telegram"), BotInstanceId.From("telegram-secondary-bot")),
        "vault://telegram/secondary",
        "secret-secondary");

    public TelegramChannelAdapter Reset(TransportMode transportMode = TransportMode.Webhook)
    {
        Api.Clear();
        _adapter = CreateAdapter(transportMode);
        _webhook = new TelegramWebhookFixture(_adapter);
        return _adapter;
    }

    public WebhookFixture Webhook => _webhook;

    public TelegramChannelAdapter Adapter => _adapter;

    private TelegramChannelAdapter CreateAdapter(TransportMode transportMode) =>
        new(
            Credentials,
            Api,
            Attachments,
            logger: NullLogger<TelegramChannelAdapter>.Instance,
            options: new TelegramChannelAdapterOptions
            {
                TransportMode = transportMode,
                RecommendedStreamDebounceMs = 3000,
            });
}

internal sealed class TelegramWebhookFixture : WebhookFixture
{
    private readonly TelegramChannelAdapter _adapter;
    private byte[]? _lastRaw;

    public TelegramWebhookFixture(TelegramChannelAdapter adapter)
    {
        _adapter = adapter;
    }

    public override async Task<ChatActivity> DispatchInboundAsync(InboundActivitySeed seed, CancellationToken ct = default)
    {
        _lastRaw = BuildPayload(seed, out _);
        using var stream = new MemoryStream(_lastRaw, writable: false);
        return await _adapter.AcceptWebhookAsync(stream, secretToken: "secret-primary", ct: ct)
               ?? throw new InvalidOperationException("Synthetic Telegram webhook did not produce an activity.");
    }

    public override async Task<ChatActivity?> ReplayLastInboundAsync(CancellationToken ct = default)
    {
        if (_lastRaw is null)
            return null;
        using var stream = new MemoryStream(_lastRaw, writable: false);
        return await _adapter.AcceptWebhookAsync(stream, secretToken: "secret-primary", ct: ct);
    }

    public override async Task<ChatActivity> DispatchInboundToBindingAsync(
        ChannelTransportBinding binding,
        InboundActivitySeed seed,
        CancellationToken ct = default)
    {
        _lastRaw = BuildPayload(seed, out _);
        using var stream = new MemoryStream(_lastRaw, writable: false);
        return await _adapter.AcceptWebhookAsync(
                   stream,
                   secretToken: binding.VerificationToken,
                   binding: binding,
                   ct: ct)
               ?? throw new InvalidOperationException("Synthetic Telegram webhook did not produce an activity.");
    }

    public override string? LastPersistedBlobRef => null;

    public override byte[]? LastRawPayloadBytes => _lastRaw;

    internal static byte[] BuildPayload(InboundActivitySeed seed, out long chatId)
    {
        chatId = seed.Scope == ConversationScope.DirectMessage
            ? PositiveDeterministicId(seed.ConversationKey)
            : -PositiveDeterministicId(seed.ConversationKey);
        var senderId = PositiveDeterministicId(seed.SenderCanonicalId);
        var updateId = PositiveDeterministicInt(seed.PlatformMessageId ?? $"{seed.ConversationKey}:{seed.Text}");
        var messageId = PositiveDeterministicInt(seed.PlatformMessageId ?? $"{seed.Text}:{seed.SenderCanonicalId}");
        var chatType = seed.Scope == ConversationScope.DirectMessage ? "private" : "group";

        var payload = new
        {
            update_id = updateId,
            message = new
            {
                message_id = messageId,
                date = 1_714_000_000,
                chat = new
                {
                    id = chatId,
                    type = chatType,
                },
                from = new
                {
                    id = senderId,
                    is_bot = false,
                    username = seed.SenderDisplayName.Replace(' ', '_'),
                    first_name = seed.SenderDisplayName,
                },
                text = seed.Text,
            },
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    internal static Update BuildUpdate(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return JsonSerializer.Deserialize<Update>(stream.ToArray(), JsonBotAPI.Options)
               ?? throw new InvalidOperationException("Unable to deserialize synthetic Telegram update.");
    }

    internal static long PositiveDeterministicId(string value)
    {
        var hash = Fnv1a64(value);
        return (long)(hash % 9_000_000_000_000UL) + 1000;
    }

    internal static int PositiveDeterministicInt(string value)
    {
        var hash = Fnv1a64(value);
        return (int)(hash % int.MaxValue) + 1;
    }

    private static ulong Fnv1a64(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}

internal sealed class FakeTelegramApiClient : ITelegramApiClient
{
    private readonly Queue<IReadOnlyList<Update>> _pollResponses = new();
    private readonly Queue<Exception> _pollExceptions = new();
    private readonly Dictionary<(long ChatId, int MessageId), FakeTelegramMessage> _messages = new();
    private int _nextMessageId = 100;
    private TaskCompletionSource<bool> _firstPollCall = CreateSignal();
    private TaskCompletionSource<bool> _firstEditCall = CreateSignal();

    public List<FakeTelegramSendCall> SendCalls { get; } = [];

    public IReadOnlyDictionary<(long ChatId, int MessageId), FakeTelegramMessage> Messages => _messages;

    public int PollCallCount { get; private set; }

    public int EditCallCount { get; private set; }

    public Task FirstPollCallAsync => _firstPollCall.Task;

    public Task FirstEditCallAsync => _firstEditCall.Task;

    public TaskCompletionSource<bool>? BlockNextEditCompletion { get; set; }

    public void EnqueuePollResponse(params Update[] updates) => _pollResponses.Enqueue(updates);

    public void EnqueuePollException(Exception exception) => _pollExceptions.Enqueue(exception);

    public void Clear()
    {
        _pollResponses.Clear();
        _pollExceptions.Clear();
        _messages.Clear();
        SendCalls.Clear();
        _nextMessageId = 100;
        PollCallCount = 0;
        EditCallCount = 0;
        BlockNextEditCompletion = null;
        _firstPollCall = CreateSignal();
        _firstEditCall = CreateSignal();
    }

    public Task<IReadOnlyList<Update>> GetUpdatesAsync(string botToken, int? offset, int timeoutSeconds, CancellationToken ct)
    {
        PollCallCount++;
        _firstPollCall.TrySetResult(true);
        if (_pollExceptions.Count > 0)
            throw _pollExceptions.Dequeue();
        if (_pollResponses.Count == 0)
            return Task.FromResult<IReadOnlyList<Update>>([]);
        return Task.FromResult(_pollResponses.Dequeue());
    }

    public Task<TelegramSentActivity> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        TelegramInlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var sent = Save(chatId, text, replyMarkup, "text", null);
        return Task.FromResult(sent);
    }

    public Task<TelegramSentActivity> SendPhotoAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        TelegramInlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var sent = Save(chatId, caption ?? string.Empty, replyMarkup, "photo", attachment.FileName);
        return Task.FromResult(sent);
    }

    public Task<TelegramSentActivity> SendDocumentAsync(
        string botToken,
        long chatId,
        TelegramAttachmentContent attachment,
        string? caption,
        TelegramInlineKeyboardMarkup? replyMarkup,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var sent = Save(chatId, caption ?? string.Empty, replyMarkup, "document", attachment.FileName);
        return Task.FromResult(sent);
    }

    public Task<TelegramSentActivity> EditMessageTextAsync(
        string botToken,
        long chatId,
        int messageId,
        string text,
        TelegramInlineKeyboardMarkup? replyMarkup,
        CancellationToken ct)
    {
        EditCallCount++;
        _firstEditCall.TrySetResult(true);
        if (BlockNextEditCompletion is not null)
        {
            var release = BlockNextEditCompletion;
            BlockNextEditCompletion = null;
            return WaitAndEditAsync(release, chatId, messageId, text, replyMarkup, ct);
        }

        _messages[(chatId, messageId)] = new FakeTelegramMessage(chatId, messageId, text, replyMarkup, _messages[(chatId, messageId)].Kind, _messages[(chatId, messageId)].AttachmentName);
        SendCalls.Add(new FakeTelegramSendCall("edit", chatId, messageId, text, replyMarkup, null));
        return Task.FromResult(new TelegramSentActivity(chatId, messageId));
    }

    public Task DeleteMessageAsync(string botToken, long chatId, int messageId, CancellationToken ct)
    {
        _messages.Remove((chatId, messageId));
        SendCalls.Add(new FakeTelegramSendCall("delete", chatId, messageId, string.Empty, null, null));
        return Task.CompletedTask;
    }

    private TelegramSentActivity Save(
        long chatId,
        string text,
        TelegramInlineKeyboardMarkup? replyMarkup,
        string kind,
        string? attachmentName)
    {
        var messageId = Interlocked.Increment(ref _nextMessageId);
        _messages[(chatId, messageId)] = new FakeTelegramMessage(chatId, messageId, text, replyMarkup, kind, attachmentName);
        SendCalls.Add(new FakeTelegramSendCall(kind, chatId, messageId, text, replyMarkup, attachmentName));
        return new TelegramSentActivity(chatId, messageId);
    }

    private async Task<TelegramSentActivity> WaitAndEditAsync(
        TaskCompletionSource<bool> release,
        long chatId,
        int messageId,
        string text,
        TelegramInlineKeyboardMarkup? replyMarkup,
        CancellationToken ct)
    {
        await release.Task.WaitAsync(ct);
        _messages[(chatId, messageId)] = new FakeTelegramMessage(chatId, messageId, text, replyMarkup, _messages[(chatId, messageId)].Kind, _messages[(chatId, messageId)].AttachmentName);
        SendCalls.Add(new FakeTelegramSendCall("edit", chatId, messageId, text, replyMarkup, null));
        return new TelegramSentActivity(chatId, messageId);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record FakeTelegramSendCall(
    string Kind,
    long ChatId,
    int MessageId,
    string Text,
    TelegramInlineKeyboardMarkup? ReplyMarkup,
    string? AttachmentName);

internal sealed record FakeTelegramMessage(
    long ChatId,
    int MessageId,
    string Text,
    TelegramInlineKeyboardMarkup? ReplyMarkup,
    string Kind,
    string? AttachmentName);

internal sealed class FakeTelegramAttachmentContentResolver : ITelegramAttachmentContentResolver
{
    public Task<TelegramAttachmentContent?> ResolveAsync(AttachmentRef attachment, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(attachment.BlobRef);
        return Task.FromResult<TelegramAttachmentContent?>(new TelegramAttachmentContent(
            FileName: string.IsNullOrWhiteSpace(attachment.Name) ? "attachment.bin" : attachment.Name,
            ContentType: string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
            Content: new MemoryStream(bytes, writable: false)));
    }
}

internal sealed class FakeCredentialProvider : ICredentialProvider
{
    private readonly IReadOnlyDictionary<string, string> _credentials;

    public FakeCredentialProvider(IReadOnlyDictionary<string, string> credentials)
    {
        _credentials = credentials;
    }

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default) =>
        Task.FromResult(_credentials.TryGetValue(credentialRef, out var value) ? value : null);
}
