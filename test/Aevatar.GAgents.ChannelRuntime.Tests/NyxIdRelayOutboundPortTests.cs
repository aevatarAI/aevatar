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
    public async Task SendAsync_ShouldRejectMissingReplyAccessToken()
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
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("missing_reply_access_token");
    }

    [Fact]
    public async Task SendAsync_ShouldPostExpectedChannelRelayReplyRequest()
    {
        var handler = new RecordingJsonHandler();
        var port = CreatePort(handler);

        var result = await port.SendAsync(
            "slack",
            BuildConversation(),
            new MessageContent { Text = "hello relay" },
            new OutboundDeliveryContext
            {
                ReplyAccessToken = "relay-token",
                ReplyMessageId = "msg-1",
            },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-1");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        handler.Requests[0].Authorization.Should().Be("Bearer relay-token");
        handler.Requests[0].Body.Should().Contain("\"message_id\":\"msg-1\"");
        handler.Requests[0].Body.Should().Contain("\"text\":\"hello relay\"");
    }

    private static NyxIdRelayOutboundPort CreatePort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            },
            NullLogger<NyxIdApiClient>.Instance);

        return new NyxIdRelayOutboundPort(client, NullLogger<NyxIdRelayOutboundPort>.Instance);
    }

    private static ConversationReference BuildConversation() =>
        ConversationReference.Create(
            ChannelId.From("slack"),
            BotInstanceId.From("reg-1"),
            ConversationScope.Group,
            "conv-1",
            "group",
            "conv-1");

    private sealed class RecordingJsonHandler : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message_id":"reply-1","platform_message_id":"platform-1"}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
