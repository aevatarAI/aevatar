using Aevatar.GAgents.Channel.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Aevatar.GAgents.Platform.Lark.Tests;

public sealed class LarkStreamingHandleTests
{
    [Fact]
    public async Task AppendAsync_OutOfOrderSequence_ReassemblesBySequenceNumber()
    {
        var harness = new LarkAdapterHarness();
        var adapter = harness.Reset();
        await adapter.InitializeAsync(harness.DefaultBinding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var handle = await adapter.BeginStreamingReplyAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("conformance-bot"),
                ConversationScope.DirectMessage,
                partition: null,
                "conformance-user"),
            new MessageContent(),
            CancellationToken.None);

        await handle.AppendAsync(new StreamChunk
        {
            Delta = "B",
            SequenceNumber = 2,
        });
        await handle.AppendAsync(new StreamChunk
        {
            Delta = "A",
            SequenceNumber = 1,
        });

        var lastId = harness.HttpHandler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        harness.HttpHandler.ReadText(lastId).ShouldBe("AB");
    }

    [Fact]
    public async Task CompleteAsync_WaitsForInFlightAppendAndPreservesFinalMessage()
    {
        var credentialProvider = new TestCredentialProvider();
        credentialProvider.Set("vault://bots/test", JsonSerializer.Serialize(new
        {
            access_token = "bot-token",
            encrypt_key = "encrypt-key",
        }));
        var httpHandler = new BlockingUpdateLarkHttpHandler();
        var adapter = new LarkChannelAdapter(
            credentialProvider,
            new LarkMessageComposer(),
            new LarkPayloadRedactor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LarkChannelAdapter>.Instance,
            new HttpClient(httpHandler)
            {
                BaseAddress = LarkChannelDefaults.DefaultBaseAddress,
            });
        var binding = ChannelTransportBinding.Create(
            ChannelBotDescriptor.Create("reg-1", ChannelId.From("lark"), BotInstanceId.From("bot-1")),
            "vault://bots/test",
            "verify-token");
        await adapter.InitializeAsync(binding, CancellationToken.None);
        await adapter.StartReceivingAsync(CancellationToken.None);

        var handle = await adapter.BeginStreamingReplyAsync(
            ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("bot-1"),
                ConversationScope.DirectMessage,
                partition: null,
                "user-open-id"),
            new MessageContent(),
            CancellationToken.None);

        var appendTask = handle.AppendAsync(new StreamChunk
        {
            Delta = "hello",
            SequenceNumber = 1,
        });
        await httpHandler.FirstUpdateStarted.Task;

        var completeTask = handle.CompleteAsync(new MessageContent
        {
            Text = "done",
        });

        httpHandler.ReleaseFirstUpdate.TrySetResult(true);
        await Task.WhenAll(appendTask, completeTask);

        var lastId = httpHandler.LastMessageId ?? throw new InvalidOperationException("No message recorded.");
        httpHandler.ReadText(lastId).ShouldBe("done");
    }

    private sealed class BlockingUpdateLarkHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, RecordedLarkMessage> _messages = new(StringComparer.Ordinal);
        private int _nextMessageId;
        private int _putCount;

        public TaskCompletionSource<bool> FirstUpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastMessageId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var messageId = $"om_{Interlocked.Increment(ref _nextMessageId)}";
                _messages[messageId] = new RecordedLarkMessage(
                    MessageId: messageId,
                    MessageType: root.GetProperty("msg_type").GetString() ?? string.Empty,
                    ContentJson: root.GetProperty("content").GetString() ?? string.Empty,
                    ReceiveId: root.GetProperty("receive_id").GetString() ?? string.Empty,
                    Deleted: false);
                LastMessageId = messageId;
                return Success(messageId);
            }

            var activityId = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            if (request.Method == HttpMethod.Put)
            {
                var putNumber = Interlocked.Increment(ref _putCount);
                if (putNumber == 1)
                {
                    FirstUpdateStarted.TrySetResult(true);
                    await ReleaseFirstUpdate.Task.WaitAsync(cancellationToken);
                }

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

            throw new InvalidOperationException($"Unsupported method: {request.Method}");
        }

        public string ReadText(string activityId)
        {
            var record = _messages[activityId];
            using var document = JsonDocument.Parse(record.ContentJson);
            return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
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
    }
}
