using System.Net;
using System.Text;
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
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        handler.Requests[0].Authorization.Should().Be("Bearer relay-token");
        handler.Requests[0].Body.Should().Contain("\"message_id\":\"msg-1\"");
        handler.Requests[0].Body.Should().Contain("\"text\":\"rendered:hello relay\"");
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
    public async Task SendAsync_ShouldRejectMissingComposer()
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

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("composer_not_found");
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
    }

    [Fact]
    public async Task UpdateAsync_ShouldMapGenericFailuresToUpdateRejected()
    {
        var handler = new RecordingJsonHandler(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_request"}""");
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

    private sealed class RecordingJsonHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = """{"message_id":"reply-1","platform_message_id":"platform-1"}""") : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
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
}
