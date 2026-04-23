using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.ChannelRuntime.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayInteractiveReplyDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_without_producer_sends_plain_text_fallback()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { message_id = "mid", platform_message_id = "pmid" }));
        var client = CreateClient(handler);
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            Array.Empty<IChannelNativeMessageProducer>());
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("telegram"),
            "msg-1",
            "relay-token",
            new MessageContent { Text = "hi there" },
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeTrue();
        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestBody!.Should().Contain("\"text\":\"hi there\"");
        handler.LastRequestBody.Should().NotContain("metadata");
    }

    [Fact]
    public async Task DispatchAsync_when_producer_reports_unsupported_falls_back_to_text()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { message_id = "mid", platform_message_id = "pmid" }));
        var client = CreateClient(handler);
        var producer = Substitute.For<IChannelNativeMessageProducer>();
        producer.Channel.Returns(ChannelId.From("lark"));
        producer.Evaluate(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>())
            .Returns(ComposeCapability.Unsupported);
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("lark"),
            "msg-2",
            "relay-token",
            new MessageContent { Text = "fallback" },
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeTrue();
        handler.LastRequestBody.Should().Contain("\"text\":\"fallback\"");
        handler.LastRequestBody.Should().NotContain("metadata");
        producer.DidNotReceive().Produce(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>());
    }

    [Fact]
    public async Task DispatchAsync_with_exact_capability_forwards_card_metadata()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { message_id = "mid", platform_message_id = "pmid" }));
        var client = CreateClient(handler);
        var producer = Substitute.For<IChannelNativeMessageProducer>();
        producer.Channel.Returns(ChannelId.From("lark"));
        producer.Evaluate(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>())
            .Returns(ComposeCapability.Exact);
        producer.Produce(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>())
            .Returns(new ChannelNativeMessage(
                Text: "fallback",
                CardPayload: new { schema = "2.0", header = "Title" },
                MessageType: "interactive",
                Capability: ComposeCapability.Exact));
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("lark"),
            "msg-3",
            "relay-token",
            new MessageContent { Text = "card intent" },
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeFalse();
        result.Capability.Should().Be(ComposeCapability.Exact);
        handler.LastRequestBody.Should().Contain("\"metadata\"");
        handler.LastRequestBody.Should().Contain("\"card\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"fallback\"");
    }

    [Fact]
    public async Task DispatchAsync_transport_failure_surfaces_detail()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.InternalServerError,
            """{"error":"boom"}""");
        var client = CreateClient(handler);
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            Array.Empty<IChannelNativeMessageProducer>());
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("telegram"),
            "msg-4",
            "relay-token",
            new MessageContent { Text = "hi" },
            new ComposeContext());

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().NotBeNull();
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(new NyxIdToolOptions { BaseUrl = "https://example.test" }, new HttpClient(handler));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseJson;

        public RecordingHandler(HttpStatusCode status, string responseJson)
        {
            _status = status;
            _responseJson = responseJson;
        }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
