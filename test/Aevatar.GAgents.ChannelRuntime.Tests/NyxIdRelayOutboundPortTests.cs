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
    public async Task SendAsync_LarkLongWorkflowReply_ShouldSendOrderedChunksWithoutDroppingFinalDraft()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler, new StubComposer("lark", text: BuildLongWorkflowReply()));

        var result = await port.SendAsync(
            "lark",
            BuildConversation(),
            new MessageContent { Text = "workflow done" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-workflow-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Should().HaveCountGreaterThan(1);
        handler.Requests.Select(request => request.Authorization).Should().OnlyContain(auth => auth == "Bearer relay-token");

        var chunks = handler.Requests.Select(request => ReadRelayReplyText(request.Body)).ToArray();
        chunks[0].Should().StartWith("[1/");
        chunks[^1].Should().StartWith($"[{chunks.Length}/{chunks.Length}]");
        chunks.Should().OnlyContain(chunk => chunk.Length <= NyxIdRelayOutboundPort.LarkReplyTextChunkLimit + 12);

        var reassembled = string.Join(
                string.Empty,
                chunks.Select(chunk => chunk[(chunk.IndexOf('\n', StringComparison.Ordinal) + 1)..]))
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        reassembled.Should().Contain("**3.Simple&Sweet**");
        reassembled.Should().Contain("Wishingyouallalovelysummer!");
        reassembled.Should().Contain("Translationdraftthreeisstillpresent.");
    }

    [Fact]
    public async Task SendAsync_NonLarkLongReply_ShouldRemainSingleShot()
    {
        var handler = new RecordingJsonHandler();
        var longReply = new string('x', NyxIdRelayOutboundPort.LarkReplyTextChunkLimit + 100);
        var port = CreatePort(handler, new StubComposer("slack", text: longReply));

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "workflow done" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-slack-workflow-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeTrue();
        AssertSingleRelayTextRequest(handler, "msg-slack-workflow-1", longReply);
    }

    [Fact]
    public async Task SendAsync_LarkLongWorkflowReply_ShouldFailObservablyWhenAnyChunkIsRejected()
    {
        var handler = new RecordingJsonHandler(
            responses:
            [
                (HttpStatusCode.OK, """{"message_id":"reply-1","platform_message_id":"platform-1"}""", null),
                (HttpStatusCode.BadRequest, """{"error":"lark_payload_too_large"}""", null),
            ]);
        var port = CreatePort(handler, new StubComposer("lark", text: BuildLongWorkflowReply()));

        var result = await port.SendAsync(
            "lark",
            BuildConversation(),
            new MessageContent { Text = "workflow done" },
            new OutboundDeliveryContext
            {
                ReplyMessageId = "msg-lark-workflow-1",
            },
            "relay-token",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_rejected");
        result.ErrorMessage.Should().Contain("chunk 2/");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void SplitReplyText_ShouldRespectSurrogatePairBoundaries()
    {
        var chunks = NyxIdRelayOutboundPort.SplitReplyText("ab\ud83d\ude00cd", 3).ToArray();

        chunks.Should().Equal("ab", "\ud83d\ude00c", "d");
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
        ReadRelayMessageId(handler.Requests[0].Body).Should().Be(expectedMessageId);
        ReadRelayReplyText(handler.Requests[0].Body).Should().Be(expectedText);
        using var document = JsonDocument.Parse(handler.Requests[0].Body);
        var reply = document.RootElement.GetProperty("reply");
        reply.TryGetProperty("metadata", out _).Should().BeFalse();
    }

    private static string ReadRelayMessageId(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return root.GetProperty("message_id").GetString() ?? string.Empty;
    }

    private static string ReadRelayReplyText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reply").GetProperty("text").GetString() ?? string.Empty;
    }

    private static string BuildLongWorkflowReply()
    {
        var paragraph = string.Join(
            " ",
            Enumerable.Repeat(
                "Thank you for the lovely update and for organizing everything for the class fund this year.",
                35));
        return string.Join(
            "\n\n",
            "**Original (EN):** " + paragraph,
            "**Translation (CN):** Class fund recap translated for parents. " + paragraph,
            "**1. Warm & Polite** Thank you so much for organizing everything. Translation draft one is present.",
            "**2. Friendly & Engaging** Thank you! It's so lovely to see how the class funds were used. Translation draft two is present.",
            "**3. Simple & Sweet** Thank you so much for everything this year. Wishing you all a lovely summer! Translation draft three is still present.");
    }

    private sealed class RecordingJsonHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = """{"message_id":"reply-1","platform_message_id":"platform-1"}""",
        TimeSpan? retryAfter = null,
        IReadOnlyList<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)>? responses = null) : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            var index = Requests.Count - 1;
            (HttpStatusCode Status, string Body, TimeSpan? RetryAfter) configured = responses is { Count: > 0 }
                ? responses[Math.Min(index, responses.Count - 1)]
                : (status, responseBody, retryAfter);
            var response = new HttpResponseMessage(configured.Status)
            {
                Content = new StringContent(configured.Body, Encoding.UTF8)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") },
                },
            };
            if (configured.RetryAfter.HasValue)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(configured.RetryAfter.Value);

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
