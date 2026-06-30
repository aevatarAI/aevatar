using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayOutboundPortTests
{
    [Fact]
    public async Task SendAsync_ShouldRejectMissingReplyToken()
    {
        var port = CreatePort(new RecordingJsonHandler());

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            string.Empty,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("reply_token_missing_or_expired");
    }

    [Fact]
    public async Task SendAsync_ShouldPostExpectedChannelRelayReplyRequest()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello relay" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-1");
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        handler.Requests[0].Authorization.Should().Be("Bearer relay-token");
        AssertSingleRelayTextRequest(handler, "msg-1", "rendered:hello relay");
    }

    [Fact]
    public async Task SendWithAgentKeyAsync_ShouldUseLongLivedAgentKeyAsBearer()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendWithAgentKeyAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "workflow done" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "bot-agent-key-1",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        handler.Requests[0].Authorization.Should().Be("Bearer bot-agent-key-1");
        AssertSingleRelayTextRequest(handler, "msg-1", "rendered:workflow done");
    }

    [Fact]
    public async Task SendWithAgentKeyAsync_LarkLongWorkflowReply_ShouldDeliverAllOrderedChunks()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("lark"));
        var longWorkflowOutput = BuildLongWorkflowOutput();

        var result = await port.SendWithAgentKeyAsync(
            "lark",
            BuildConversation(),
            new MessageContent { Text = longWorkflowOutput },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-workflow-1",
            },
            "bot-agent-key-1",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().HaveCountGreaterThan(1);
        handler.Requests.Select(request => request.Authorization)
            .Should()
            .OnlyContain(value => value == "Bearer bot-agent-key-1");
        handler.Requests.Select(request => request.Path)
            .Should()
            .OnlyContain(path => path == "/api/v1/channel-relay/reply");

        var delivered = handler.Requests.Select(ExtractReplyText).ToArray();
        delivered[0].Should().Contain($"[part 1/{delivered.Length} continues]");
        delivered[^1].Should().Contain($"[part {delivered.Length}/{delivered.Length} continued]");
        delivered.Should().OnlyContain(chunk => chunk.Length <= 3_500);

        var combined = string.Join("\n\n", delivered);
        combined.Should().Contain("**1. Warm & Polite**");
        combined.Should().Contain("**2. Friendly & Engaging**");
        combined.Should().Contain("**3. Simple & Sweet**");
        combined.Should().Contain("Wishing you all a lovely summer!");
        combined.Should().Contain("CN draft 3 line delivered.");
    }

    [Fact]
    public async Task SendWithAgentKeyAsync_LarkChunkRejection_ShouldFailObservably()
    {
        var handler = new RecordingJsonHandler(
            requestIndex => requestIndex == 2
                ? (HttpStatusCode.BadRequest, """{"error":"message_too_large"}""")
                : (HttpStatusCode.OK, """{"message_id":"reply-1","platform_message_id":"platform-1"}"""));
        var port = CreatePort(handler, new StubComposer("lark"));

        var result = await port.SendWithAgentKeyAsync(
            "lark",
            BuildConversation(),
            new MessageContent { Text = BuildLongWorkflowOutput() },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-workflow-1",
            },
            "bot-agent-key-1",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_chunk_rejected");
        result.ErrorMessage.Should().Contain("message_too_large");
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendWithAgentKeyAsync_ShouldRejectMissingAgentKeyWithoutHttpRequest(string agentKey)
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendWithAgentKeyAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "workflow done" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            agentKey,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("bot_agent_key_missing");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_LarkInteractiveContent_ShouldUseComposerPlainText()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("lark", text: "composer only kept top text"));
        var content = new MessageContent { Text = "Choose route" };
        var card = new CardBlock
        {
            Title = "Model settings",
            Text = "Current: no service selected",
        };
        card.Fields.Add(new CardField { Title = "Service", Text = "openai" });
        content.Cards.Add(card);
        var select = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = "service",
            Label = "Select service",
        };
        select.Options.Add(new ActionOption { Label = "OpenAI", Value = "openai" });
        select.Options.Add(new ActionOption { Label = "Azure OpenAI", Value = "azure-openai" });
        content.Actions.Add(select);

        var result = await port.SendAsync(
            "lark",
            BuildConversation(),
            content,
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-options-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Contain("\"message_id\":\"msg-lark-options-1\"");
        using var document = JsonDocument.Parse(handler.Requests[0].Body);
        var text = document.RootElement.GetProperty("reply").GetProperty("text").GetString();
        text.Should().Be("composer only kept top text");
        handler.Requests[0].Body.Should().NotContain("metadata");
    }

    [Fact]
    public async Task SendAsync_ShouldRejectMissingReplyMessageId()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext
            {
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("missing_reply_message_id");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldRejectMissingPlatform()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendAsync(
            "",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("platform_required");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldRejectEmptyRenderedText()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack", text: ""));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("empty_reply");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenNyxRejectsRelayReply()
    {
        var handler = new RecordingJsonHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_reply_token\"}");
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_rejected");
    }

    [Fact]
    public async Task SendAsync_ShouldUsePlainTextFallbackWhenComposerIsMissing()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler);

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello relay" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        AssertSingleRelayTextRequest(handler, "msg-1", "hello relay");
    }

    [Fact]
    public async Task SendAsync_ShouldFlattenCardAndActionsWhenComposerIsMissing()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler);
        var content = BuildInteractiveContent();

        var result = await port.SendAsync(
            "lark",
            BuildConversation(),
            content,
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-options-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        AssertSingleRelayTextRequest(
            handler,
            "msg-lark-options-1",
            string.Join(
                "\n",
                "Choose route",
                "Model settings",
                "Current: no service selected",
                "Service: openai",
                "\u2022 Select service",
                "  - OpenAI",
                "  - Azure OpenAI",
                "\u2022 Refresh"));
    }

    [Fact]
    public async Task SendAsync_ShouldRejectEmptyPlainTextFallbackWithoutHttpRequest()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler);

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent(),
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("empty_reply");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldRejectUnsupportedComposeCapability()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack", ComposeCapability.Unsupported));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello relay" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("composer_unsupported");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldRejectRegisteredComposerWithoutPlainTextPayload()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new NonPlainTextComposer("slack"));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello relay" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("plain_text_payload_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldSurfacePlatformMessageId()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.OK,
            """{"message_id":"reply-1","platform_message_id":"om_abc"}""");
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.PlatformMessageId.Should().Be("om_abc");
    }

    [Fact]
    public async Task UpdateAsync_ShouldPostUpdateEndpointAndSurfaceSuccess()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.OK,
            """{"upstream_message_id":"om_abc","edited_at":"2026-04-24T09:00:00Z"}""");
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: "om_abc",
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("nyx-relay-update:om_abc");
        result.PlatformMessageId.Should().Be("om_abc");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply/update");
        handler.Requests[0].Body.Should().Contain("\"message_id\":\"om_abc\"");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectMissingReplyToken()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: "om_abc",
            " ",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("reply_token_missing_or_expired");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectMissingPlatformMessageId()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: " ",
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("missing_platform_message_id");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ShouldMap501ToEditUnsupportedErrorCode()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.NotImplemented,
            """{"code":"edit_unsupported","message":"platform does not support edits"}""");
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: "om_abc",
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_edit_unsupported");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.HttpStatus.Should().Be(501);
        result.RawErrorKey.Should().Be("edit_unsupported");
    }

    [Fact]
    public async Task UpdateAsync_ShouldMapGenericFailuresToUpdateRejectedWithTypedDiagnostics()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.BadRequest,
            """{"error":"validation_error","error_code":1008}""");
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: "om_abc",
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_update_rejected");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.HttpStatus.Should().Be(400);
        result.RawErrorKey.Should().Be("validation_error");
        result.RawErrorCode.Should().Be(1008);
    }

    [Fact]
    public async Task UpdateAsync_ShouldPreserveTransientDiagnostics()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.TooManyRequests,
            """{"error":"rate_limited","error_code":1005}""",
            retryAfter: TimeSpan.FromSeconds(3));
        var port = CreatePort(handler, new StubComposer("slack"));

        var result = await port.UpdateAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello" },
            new OutboundDeliveryContext { ReplyMessageId = "msg-1" },
            platformMessageId: "om_abc",
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_update_rejected");
        result.FailureKind.Should().Be(FailureKind.TransientAdapterError);
        result.RetryAfterTimeSpan.Should().Be(TimeSpan.FromSeconds(3));
        result.HttpStatus.Should().Be(429);
        result.RawErrorKey.Should().Be("rate_limited");
        result.RawErrorCode.Should().Be(1005);
    }

    private static NyxIdRelayOutboundPort CreatePort(HttpMessageHandler handler, params IMessageComposer[] composers)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            },
            NullLogger<NyxIdApiClient>.Instance);

        return new NyxIdRelayOutboundPort(client, NullLogger<NyxIdRelayOutboundPort>.Instance, composers);
    }

    private static ConversationReference BuildConversation() =>
        ConversationReference.Create(
            ChannelId.From("slack"),
            BotInstanceId.From("reg-1"),
            ConversationScope.Group,
            "conv-1",
            "group",
            "conv-1");

    private static MessageContent BuildInteractiveContent()
    {
        var content = new MessageContent { Text = "Choose route" };
        var card = new CardBlock
        {
            Title = "Model settings",
            Text = "Current: no service selected",
        };
        card.Fields.Add(new CardField { Title = "Service", Text = "openai" });
        card.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "refresh",
            Label = "Refresh",
        });
        content.Cards.Add(card);
        var select = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = "service",
            Label = "Select service",
        };
        select.Options.Add(new ActionOption { Label = "OpenAI", Value = "openai" });
        select.Options.Add(new ActionOption { Label = "Azure OpenAI", Value = "azure-openai" });
        content.Actions.Add(select);

        return content;
    }

    private static void AssertSingleRelayTextRequest(
        RecordingJsonHandler handler,
        string expectedMessageId,
        string expectedText)
    {
        handler.Requests.Should().ContainSingle();
        using var document = JsonDocument.Parse(handler.Requests[0].Body);
        var root = document.RootElement;
        root.GetProperty("message_id").GetString().Should().Be(expectedMessageId);
        var reply = root.GetProperty("reply");
        reply.GetProperty("text").GetString().Should().Be(expectedText);
        reply.TryGetProperty("metadata", out _).Should().BeFalse();
    }

    private static string ExtractReplyText((string Path, string? Authorization, string Body) request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("reply").GetProperty("text").GetString() ?? string.Empty;
    }

    private static string BuildLongWorkflowOutput()
    {
        var repeatedContext = string.Concat(Enumerable.Repeat(
            "Class funds covered classroom supplies, shared activities, and year-end coordination. ",
            34));
        return string.Join(
            "\n\n",
            "**Original (EN):** Hi Everyone! Below is a quick recap on how we used our class funds this year. " +
            repeatedContext,
            "**Translation (CN):** Class funds summary translated for parents. " +
            string.Concat(Enumerable.Repeat("Translation detail preserved for relay delivery. ", 240)),
            "**Suggested replies:**",
            "**1. Warm & Polite**\nEN: Thank you so much for organizing everything and sharing such a thoughtful recap.\nCN: Warm polite translation line.",
            "**2. Friendly & Engaging**\nEN: Thank you! It's so lovely to see how the class funds were used throughout the year. Have a wonderful summer, everyone!\nCN: Friendly engaging translation line.",
            "**3. Simple & Sweet**\nEN: Thank you so much for everything this year. Wishing you all a lovely summer!\nCN: CN draft 3 line delivered.");
    }

    private sealed class RecordingJsonHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = """{"message_id":"reply-1","platform_message_id":"platform-1"}""",
        TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        private readonly Func<int, (HttpStatusCode Status, string Body)>? _responseByRequest;

        public RecordingJsonHandler(Func<int, (HttpStatusCode Status, string Body)> responseByRequest)
            : this()
        {
            _responseByRequest = responseByRequest ?? throw new ArgumentNullException(nameof(responseByRequest));
        }

        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestIndex = Requests.Count + 1;
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            var effective = _responseByRequest?.Invoke(requestIndex) ?? (status, responseBody);

            var response = new HttpResponseMessage(effective.Status)
            {
                Content = new StringContent(effective.Body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter.HasValue)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);

            return response;
        }
    }

    private sealed class StubComposer(
        string platform,
        ComposeCapability capability = ComposeCapability.Exact,
        string? text = null)
        : IMessageComposer<StubNativePayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public StubNativePayload Compose(MessageContent intent, ComposeContext context) =>
            new(text ?? $"rendered:{intent.Text}");

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => capability;
    }

    private sealed record StubNativePayload(string PlainText) : IPlainTextComposedMessage;

    private sealed class NonPlainTextComposer(string platform) : IMessageComposer<object>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public object Compose(MessageContent intent, ComposeContext context) => new();

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => ComposeCapability.Exact;
    }
}
