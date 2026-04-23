using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Platform.Lark.Tests;

internal sealed class LarkAdapterHarness
{
    private const string CredentialRef = "vault://bots/lark-conformance";
    private const string VerificationToken = "verify-token";
    private const string EncryptKey = "encrypt-key";

    public ChannelTransportBinding DefaultBinding { get; } = ChannelTransportBinding.Create(
        ChannelBotDescriptor.Create(
            registrationId: "lark-reg-1",
            channel: ChannelId.From("lark"),
            bot: BotInstanceId.From("lark-bot")),
        credentialRef: CredentialRef,
        verificationToken: VerificationToken);

    public TestCredentialProvider CredentialProvider { get; private set; } = null!;

    public RecordingLarkHttpHandler HttpHandler { get; private set; } = null!;

    public LarkWebhookFixture Webhook { get; private set; } = null!;

    public LarkPayloadRedactor Redactor { get; private set; } = null!;

    public LarkStreamingProbe StreamingProbe { get; private set; } = null!;

    public LarkChannelAdapter Reset()
    {
        HttpHandler = new RecordingLarkHttpHandler();
        CredentialProvider = new TestCredentialProvider();
        CredentialProvider.Set(CredentialRef, JsonSerializer.Serialize(new
        {
            access_token = "bot-access-token",
            encrypt_key = EncryptKey,
        }));
        CredentialProvider.Set("vault://users/delegate", JsonSerializer.Serialize(new
        {
            access_token = "user-access-token",
        }));

        Redactor = new LarkPayloadRedactor();
        var adapter = new LarkChannelAdapter(
            CredentialProvider,
            new LarkMessageComposer(),
            Redactor,
            NullLogger<LarkChannelAdapter>.Instance,
            new HttpClient(HttpHandler)
            {
                BaseAddress = LarkChannelDefaults.DefaultBaseAddress,
            });
        Webhook = new LarkWebhookFixture(adapter, DefaultBinding, EncryptKey);
        StreamingProbe = new LarkStreamingProbe(adapter, HttpHandler);
        return adapter;
    }
}

internal sealed class RecordingLarkHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, RecordedLarkMessage> _messages = new(StringComparer.Ordinal);
    private int _nextMessageId;

    public string? LastReceiveIdType { get; private set; }

    public string? LastAuthorization { get; private set; }

    public string? LastPath { get; private set; }

    public string? LastMessageId { get; private set; }

    public IReadOnlyDictionary<string, RecordedLarkMessage> Messages => _messages;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.PathAndQuery;
        LastAuthorization = request.Headers.Authorization?.Parameter;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        if (request.Method == HttpMethod.Post)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var messageId = $"om_{Interlocked.Increment(ref _nextMessageId)}";
            var record = new RecordedLarkMessage(
                MessageId: messageId,
                MessageType: root.GetProperty("msg_type").GetString() ?? string.Empty,
                ContentJson: root.GetProperty("content").GetString() ?? string.Empty,
                ReceiveId: root.GetProperty("receive_id").GetString() ?? string.Empty,
                Deleted: false);
            LastReceiveIdType = ParseReceiveIdType(request.RequestUri?.Query);
            _messages[messageId] = record;
            LastMessageId = messageId;
            return Success(messageId);
        }

        var activityId = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        if (request.Method == HttpMethod.Put)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            _messages[activityId] = new RecordedLarkMessage(
                MessageId: activityId,
                MessageType: root.GetProperty("msg_type").GetString() ?? string.Empty,
                ContentJson: root.GetProperty("content").GetString() ?? string.Empty,
                ReceiveId: _messages.TryGetValue(activityId, out var existing) ? existing.ReceiveId : string.Empty,
                Deleted: false);
            LastMessageId = activityId;
            return Success(activityId);
        }

        if (request.Method == HttpMethod.Delete)
        {
            if (_messages.TryGetValue(activityId, out var existing))
                _messages[activityId] = existing with { Deleted = true };
            LastMessageId = activityId;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0,\"msg\":\"success\"}", Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    public string ReadText(string activityId)
    {
        var record = _messages[activityId];
        using var document = JsonDocument.Parse(record.ContentJson);
        if (record.MessageType == "text")
            return document.RootElement.GetProperty("text").GetString() ?? string.Empty;

        return document.RootElement.ToString();
    }

    private static HttpResponseMessage Success(string messageId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                code = 0,
                msg = "success",
                data = new
                {
                    message_id = messageId,
                },
            }),
            Encoding.UTF8,
            "application/json"),
    };

    private static string? ParseReceiveIdType(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "receive_id_type", StringComparison.Ordinal))
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }
}

internal sealed record RecordedLarkMessage(
    string MessageId,
    string MessageType,
    string ContentJson,
    string ReceiveId,
    bool Deleted);

internal sealed class TestCredentialProvider : ICredentialProvider
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string credentialRef, string secret) => _values[credentialRef] = secret;

    public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default) =>
        Task.FromResult<string?>(_values.TryGetValue(credentialRef, out var value) ? value : null);
}

internal sealed class LarkWebhookFixture(
    LarkChannelAdapter adapter,
    ChannelTransportBinding binding,
    string encryptKey) : WebhookFixture
{
    private byte[]? _lastBody;
    private Dictionary<string, string>? _lastHeaders;
    private string? _lastPersistedBlobRef;
    private byte[]? _lastRawPayloadBytes;

    public override string? LastPersistedBlobRef => _lastPersistedBlobRef;

    public override byte[]? LastRawPayloadBytes => _lastRawPayloadBytes;

    public override async Task<ChatActivity> DispatchInboundAsync(InboundActivitySeed seed, CancellationToken ct = default)
    {
        var chatType = seed.Scope == ConversationScope.Group ? "group" : "p2p";
        var payload = new
        {
            schema = "2.0",
            header = new
            {
                event_type = "im.message.receive_v1",
                token = binding.VerificationToken,
            },
            @event = new
            {
                sender = new
                {
                    sender_id = new
                    {
                        open_id = seed.SenderCanonicalId,
                    },
                    sender_type = "user",
                },
                message = new
                {
                    chat_id = seed.Scope == ConversationScope.Group ? seed.ConversationKey : $"chat-{seed.SenderCanonicalId}",
                    message_id = seed.PlatformMessageId ?? $"msg-{Guid.NewGuid():N}",
                    message_type = "text",
                    chat_type = chatType,
                    content = JsonSerializer.Serialize(new
                    {
                        text = seed.Text,
                    }),
                    create_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                },
            },
        };

        _lastBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        _lastHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Lark-Request-Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["X-Lark-Request-Nonce"] = "nonce",
        };
        _lastHeaders["X-Lark-Signature"] = LarkChannelAdapter.ComputeLarkSignature(
            _lastHeaders["X-Lark-Request-Timestamp"],
            "nonce",
            encryptKey,
            Encoding.UTF8.GetString(_lastBody));

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(_lastBody, _lastHeaders), ct);
        _lastPersistedBlobRef = response.Activity?.RawPayloadBlobRef;
        _lastRawPayloadBytes = response.SanitizedPayload;
        return response.Activity ?? throw new InvalidOperationException("Expected one ChatActivity.");
    }

    public override async Task<ChatActivity?> ReplayLastInboundAsync(CancellationToken ct = default)
    {
        if (_lastBody is null || _lastHeaders is null)
            return null;

        var response = await adapter.HandleWebhookAsync(new LarkWebhookRequest(_lastBody, _lastHeaders), ct);
        _lastPersistedBlobRef = response.Activity?.RawPayloadBlobRef;
        _lastRawPayloadBytes = response.SanitizedPayload;
        return response.Activity;
    }
}

internal sealed class LarkStreamingProbe(
    LarkChannelAdapter adapter,
    RecordingLarkHttpHandler handler) : StreamingFaultProbe
{
    private static readonly ConversationReference Reference = ConversationReference.Create(
        ChannelId.From("lark"),
        BotInstanceId.From("conformance-bot"),
        ConversationScope.DirectMessage,
        partition: null,
        "conformance-user");

    public override async Task<bool> DisposeWithoutCompleteMarksInterruptedAsync(CancellationToken ct = default)
    {
        var handle = await adapter.BeginStreamingReplyAsync(Reference, SampleMessageContent.SimpleText("seed"), ct);
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "partial",
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
            Delta = "later",
            SequenceNumber = 1,
        });
        await handle.CompleteAsync(SampleMessageContent.SimpleText("done"));

        var lastId = handler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        return handler.ReadText(lastId).Contains("done", StringComparison.Ordinal);
    }

    public override async Task<bool> AppendIdempotentBySequenceNumberAsync(CancellationToken ct = default)
    {
        var handle = await adapter.BeginStreamingReplyAsync(Reference, SampleMessageContent.SimpleText(string.Empty), ct);
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
        await handle.CompleteAsync(SampleMessageContent.SimpleText("AA"));

        var lastId = handler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        return handler.ReadText(lastId) == "AA";
    }
}

internal static class LarkPayloadEncryption
{
    public static string Encrypt(string plaintext, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = "0123456789abcdef"u8.ToArray();
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plaintext), 0, Encoding.UTF8.GetByteCount(plaintext));
        return Convert.ToBase64String(iv.Concat(cipher).ToArray());
    }
}
