using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Telegram;
using Aevatar.GAgents.Channel.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.Telegram.Tests;

internal sealed class TelegramAdapterHarness
{
    private const string PrimaryCredentialRef = "vault://telegram/primary";
    private const string SecondaryCredentialRef = "vault://telegram/secondary";

    public ChannelTransportBinding DefaultBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create(
            registrationId: "telegram-primary",
            channel: ChannelId.From("telegram"),
            bot: BotInstanceId.From("telegram-primary-bot")),
        credentialRef: PrimaryCredentialRef,
        verificationToken: "secret-primary");

    public ChannelTransportBinding SecondaryBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create(
            registrationId: "telegram-secondary",
            channel: ChannelId.From("telegram"),
            bot: BotInstanceId.From("telegram-secondary-bot")),
        credentialRef: SecondaryCredentialRef,
        verificationToken: "secret-secondary");

    public TestCredentialProvider CredentialProvider { get; private set; } = null!;

    public RecordingTelegramHttpHandler HttpHandler { get; private set; } = null!;

    public FakeTelegramAttachmentContentResolver AttachmentResolver { get; private set; } = null!;

    public TelegramWebhookFixture Webhook { get; private set; } = null!;

    public TelegramPayloadRedactor Redactor { get; private set; } = null!;

    public TelegramStreamingProbe StreamingProbe { get; private set; } = null!;

    public TelegramChannelAdapter Reset(TransportMode transportMode = TransportMode.Webhook)
    {
        HttpHandler = new RecordingTelegramHttpHandler();
        CredentialProvider = new TestCredentialProvider();
        CredentialProvider.Set(PrimaryCredentialRef, "bot-token-primary");
        CredentialProvider.Set(SecondaryCredentialRef, "bot-token-secondary");
        AttachmentResolver = new FakeTelegramAttachmentContentResolver();
        Redactor = new TelegramPayloadRedactor();

        var adapter = new TelegramChannelAdapter(
            CredentialProvider,
            new TelegramMessageComposer(),
            Redactor,
            NullLogger<TelegramChannelAdapter>.Instance,
            new HttpClient(HttpHandler)
            {
                BaseAddress = TelegramChannelDefaults.DefaultBaseAddress,
            },
            AttachmentResolver,
            new TelegramChannelAdapterOptions
            {
                TransportMode = transportMode,
                RecommendedStreamDebounceMs = 10,
            });

        Webhook = new TelegramWebhookFixture(adapter, DefaultBinding);
        StreamingProbe = new TelegramStreamingProbe(adapter, HttpHandler);
        return adapter;
    }
}

internal sealed class RecordingTelegramHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<(long ChatId, int MessageId), RecordedTelegramMessage> _messages = new();
    private readonly Queue<TelegramPollOutcome> _pollOutcomes = new();
    private int _nextMessageId = 100;
    private TaskCompletionSource<bool> _firstPollCall = CreateSignal();
    private TaskCompletionSource<bool> _firstEditCall = CreateSignal();

    public IReadOnlyDictionary<(long ChatId, int MessageId), RecordedTelegramMessage> Messages => _messages;

    public string? LastBotToken { get; private set; }

    public string? LastMethodName { get; private set; }

    public string? LastPath { get; private set; }

    public string? LastMessageId { get; private set; }

    public int PollCallCount { get; private set; }

    public int EditCallCount { get; private set; }

    public Task FirstPollCallAsync => _firstPollCall.Task;

    public Task FirstEditCallAsync => _firstEditCall.Task;

    public TaskCompletionSource<bool>? BlockNextEditCompletion { get; set; }

    public void EnqueuePollResponse(params byte[][] updates) =>
        _pollOutcomes.Enqueue(TelegramPollOutcome.Success(updates.Select(Encoding.UTF8.GetString).ToArray()));

    public void EnqueuePollFailure(int statusCode, string description) =>
        _pollOutcomes.Enqueue(TelegramPollOutcome.Failure(statusCode, description));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        (LastBotToken, LastMethodName) = ParseRoute(request.RequestUri);
        LastPath = request.RequestUri?.PathAndQuery;

        return LastMethodName switch
        {
            "getUpdates" => HandleGetUpdates(),
            "sendMessage" => await HandleSendAsync(request, "sendMessage", cancellationToken),
            "sendPhoto" => await HandleSendAsync(request, "sendPhoto", cancellationToken),
            "sendDocument" => await HandleSendAsync(request, "sendDocument", cancellationToken),
            "editMessageText" => await HandleEditAsync(request, cancellationToken),
            "deleteMessage" => await HandleDeleteAsync(request, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error_code = 400,
                        description = $"unsupported method {LastMethodName}",
                    }),
                    Encoding.UTF8,
                    "application/json"),
            },
        };
    }

    public string ReadText(string activityId)
    {
        var (chatId, messageId) = ParseActivityId(activityId);
        return _messages[(chatId, messageId)].Text;
    }

    private HttpResponseMessage HandleGetUpdates()
    {
        PollCallCount++;
        _firstPollCall.TrySetResult(true);

        if (_pollOutcomes.Count == 0)
            return Ok(new { ok = true, result = Array.Empty<object>() });

        var outcome = _pollOutcomes.Dequeue();
        if (outcome.StatusCode.HasValue)
        {
            return new HttpResponseMessage((HttpStatusCode)outcome.StatusCode.Value)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error_code = outcome.StatusCode.Value,
                        description = outcome.Description,
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        var updates = outcome.UpdateJsonPayloads!.Select(static payload => JsonDocument.Parse(payload).RootElement.Clone()).ToArray();
        return Ok(new
        {
            ok = true,
            result = updates,
        });
    }

    private async Task<HttpResponseMessage> HandleSendAsync(
        HttpRequestMessage request,
        string methodName,
        CancellationToken cancellationToken)
    {
        var root = await ReadJsonAsync(request, cancellationToken);
        var chatId = root.GetProperty("chat_id").GetInt64();
        var text = TryReadString(root, "text") ?? TryReadString(root, "caption") ?? string.Empty;
        var replyMarkupJson = root.TryGetProperty("reply_markup", out var replyMarkup)
            ? replyMarkup.GetRawText()
            : null;
        var messageId = Interlocked.Increment(ref _nextMessageId);
        _messages[(chatId, messageId)] = new RecordedTelegramMessage(
            ChatId: chatId,
            MessageId: messageId,
            MethodName: methodName,
            Text: text,
            ReplyMarkupJson: replyMarkupJson,
            Deleted: false);
        LastMessageId = BuildActivityId(chatId, messageId);

        return Ok(new
        {
            ok = true,
            result = new
            {
                message_id = messageId,
                chat = new
                {
                    id = chatId,
                },
            },
        });
    }

    private async Task<HttpResponseMessage> HandleEditAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EditCallCount++;
        _firstEditCall.TrySetResult(true);

        var blocker = BlockNextEditCompletion;
        BlockNextEditCompletion = null;
        if (blocker is not null)
            await blocker.Task.WaitAsync(cancellationToken);

        var root = await ReadJsonAsync(request, cancellationToken);
        var chatId = root.GetProperty("chat_id").GetInt64();
        var messageId = root.GetProperty("message_id").GetInt32();
        var text = root.GetProperty("text").GetString() ?? string.Empty;
        var replyMarkupJson = root.TryGetProperty("reply_markup", out var replyMarkup)
            ? replyMarkup.GetRawText()
            : null;
        _messages[(chatId, messageId)] = new RecordedTelegramMessage(
            ChatId: chatId,
            MessageId: messageId,
            MethodName: "editMessageText",
            Text: text,
            ReplyMarkupJson: replyMarkupJson,
            Deleted: false);
        LastMessageId = BuildActivityId(chatId, messageId);

        return Ok(new
        {
            ok = true,
            result = new
            {
                message_id = messageId,
                chat = new
                {
                    id = chatId,
                },
            },
        });
    }

    private async Task<HttpResponseMessage> HandleDeleteAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var root = await ReadJsonAsync(request, cancellationToken);
        var chatId = root.GetProperty("chat_id").GetInt64();
        var messageId = root.GetProperty("message_id").GetInt32();
        if (_messages.TryGetValue((chatId, messageId), out var existing))
            _messages[(chatId, messageId)] = existing with { Deleted = true };
        LastMessageId = BuildActivityId(chatId, messageId);

        return Ok(new
        {
            ok = true,
            result = true,
        });
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static (string BotToken, string MethodName) ParseRoute(Uri? uri)
    {
        var segments = uri?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
        if (segments.Length < 2 || !segments[0].StartsWith("bot", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected Telegram request path: {uri}");

        return (segments[0]["bot".Length..], segments[1]);
    }

    private static (long ChatId, int MessageId) ParseActivityId(string activityId)
    {
        var parts = activityId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 4 &&
            string.Equals(parts[0], "telegram", StringComparison.Ordinal) &&
            string.Equals(parts[1], "message", StringComparison.Ordinal) &&
            long.TryParse(parts[2], out var chatId) &&
            int.TryParse(parts[3], out var messageId))
        {
            return (chatId, messageId);
        }

        throw new InvalidOperationException($"Unexpected Telegram activity id: {activityId}");
    }

    private static string BuildActivityId(long chatId, int messageId) => $"telegram:message:{chatId}:{messageId}";

    private static HttpResponseMessage Ok(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record TelegramPollOutcome(string[]? UpdateJsonPayloads, int? StatusCode, string? Description)
    {
        public static TelegramPollOutcome Success(string[] payloads) => new(payloads, null, null);

        public static TelegramPollOutcome Failure(int statusCode, string description) => new(null, statusCode, description);
    }
}

internal sealed record RecordedTelegramMessage(
    long ChatId,
    int MessageId,
    string MethodName,
    string Text,
    string? ReplyMarkupJson,
    bool Deleted);

internal sealed class TelegramWebhookFixture(
    TelegramChannelAdapter adapter,
    ChannelTransportBinding defaultBinding) : WebhookFixture
{
    private byte[]? _lastBody;
    private Dictionary<string, string>? _lastHeaders;
    private string? _lastPersistedBlobRef;
    private byte[]? _lastRawPayloadBytes;

    public override string? LastPersistedBlobRef => _lastPersistedBlobRef;

    public override byte[]? LastRawPayloadBytes => _lastRawPayloadBytes;

    public override Task<ChatActivity> DispatchInboundAsync(InboundActivitySeed seed, CancellationToken ct = default) =>
        DispatchInboundToBindingAsync(defaultBinding, seed, ct);

    public override async Task<ChatActivity?> ReplayLastInboundAsync(CancellationToken ct = default)
    {
        if (_lastBody is null || _lastHeaders is null)
            return null;

        var response = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(_lastBody, _lastHeaders), ct);
        _lastPersistedBlobRef = response.Activity?.RawPayloadBlobRef;
        _lastRawPayloadBytes = response.SanitizedPayload;
        return response.Activity;
    }

    public override async Task<ChatActivity> DispatchInboundToBindingAsync(
        ChannelTransportBinding binding,
        InboundActivitySeed seed,
        CancellationToken ct = default)
    {
        _lastBody = BuildPayload(seed, out _);
        _lastHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TelegramChannelDefaults.SecretHeaderName] = binding.VerificationToken,
        };

        var response = await adapter.HandleWebhookAsync(new TelegramWebhookRequest(_lastBody, _lastHeaders), ct);
        _lastPersistedBlobRef = response.Activity?.RawPayloadBlobRef;
        _lastRawPayloadBytes = response.SanitizedPayload;
        return response.Activity ?? throw new InvalidOperationException("Expected one ChatActivity.");
    }

    internal static byte[] BuildPayload(InboundActivitySeed seed, out long chatId)
    {
        chatId = seed.Scope == ConversationScope.DirectMessage
            ? PositiveDeterministicId(seed.ConversationKey)
            : -PositiveDeterministicId(seed.ConversationKey);
        var senderId = PositiveDeterministicId(seed.SenderCanonicalId);
        var updateId = PositiveDeterministicInt(seed.PlatformMessageId ?? $"{seed.ConversationKey}:{seed.Text}");
        var messageId = PositiveDeterministicInt(seed.PlatformMessageId ?? $"{seed.Text}:{seed.SenderCanonicalId}");
        var chatType = seed.Scope == ConversationScope.DirectMessage ? "private" : "group";

        return JsonSerializer.SerializeToUtf8Bytes(new
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
        });
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

internal sealed class TelegramStreamingProbe(
    TelegramChannelAdapter adapter,
    RecordingTelegramHttpHandler handler) : StreamingFaultProbe
{
    private static readonly ConversationReference Reference =
        ConversationReference.TelegramPrivate(BotInstanceId.From("telegram-primary-bot"), "42");

    public override async Task<bool> DisposeWithoutCompleteMarksInterruptedAsync(CancellationToken ct = default)
    {
        var handle = await adapter.BeginStreamingReplyAsync(Reference, SampleMessageContent.SimpleText("seed"), ct);
        await handle.AppendAsync(new StreamChunk
        {
            Delta = " partial",
            SequenceNumber = 1,
        });
        await handle.DisposeAsync();

        var lastId = handler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        return handler.ReadText(lastId).Contains("reply interrupted", StringComparison.Ordinal);
    }

    public override async Task<bool> IntentDegradesMidwayReachesTerminalStateAsync(CancellationToken ct = default)
    {
        var handle = await adapter.BeginStreamingReplyAsync(Reference, SampleMessageContent.TextWithCard("seed"), ct);
        await handle.AppendAsync(new StreamChunk
        {
            Delta = " later",
            SequenceNumber = 1,
        });
        await handle.CompleteAsync(SampleMessageContent.SimpleText("done"));

        var lastId = handler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        return string.Equals(handler.ReadText(lastId), "done", StringComparison.Ordinal);
    }

    public override async Task<bool> AppendIdempotentBySequenceNumberAsync(CancellationToken ct = default)
    {
        var handle = await adapter.BeginStreamingReplyAsync(Reference, SampleMessageContent.SimpleText("seed"), ct);
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "A",
            SequenceNumber = 1,
        });
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "A",
            SequenceNumber = 1,
        });
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "A",
            SequenceNumber = 2,
        });
        await handle.CompleteAsync(SampleMessageContent.SimpleText("seedAA"));

        var lastId = handler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        return string.Equals(handler.ReadText(lastId), "seedAA", StringComparison.Ordinal);
    }
}

internal sealed class FakeTelegramAttachmentContentResolver : ITelegramAttachmentContentResolver
{
    public Task<TelegramAttachmentContent?> ResolveAsync(AttachmentRef attachment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return Task.FromResult<TelegramAttachmentContent?>(new TelegramAttachmentContent(
            FileName: string.IsNullOrWhiteSpace(attachment.Name) ? "attachment.bin" : attachment.Name,
            ContentType: string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
            Content: null,
            ExternalUrl: $"https://cdn.example.com/{Uri.EscapeDataString(attachment.AttachmentId)}"));
    }
}

internal sealed class TestCredentialProvider : ICredentialProvider
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string credentialRef, string secret) => _values[credentialRef] = secret;

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default) =>
        Task.FromResult<string?>(_values.TryGetValue(credentialRef, out var value) ? value : null);
}

internal sealed class ThrowingRedactor : IPayloadRedactor
{
    public Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct) =>
        throw new InvalidOperationException("redactor boom");

    public Task<HealthStatus> HealthCheckAsync(CancellationToken ct) =>
        Task.FromResult(HealthStatus.Unhealthy);
}
