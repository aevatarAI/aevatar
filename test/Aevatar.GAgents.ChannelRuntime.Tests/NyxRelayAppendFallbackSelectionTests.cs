using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAppendFallbackSelectionTests
{
    [Fact]
    public async Task DispatchAsync_NoProducerDefersFlattenedTextWithoutConsumingReplyToken()
    {
        var fixture = Create();
        var intent = new MessageContent { Text = "Choose" };
        intent.Cards.Add(new CardBlock { Title = "Options", Text = "First option" });

        var result = await fixture.Dispatcher.DispatchAsync(ChannelId.From("unknown-chat"),
            "inbound-alpha", "reply-token", intent, new ComposeContext(), deferTextDelivery: true);

        AssertDeferred(result, fixture.Handler, "Choose\nOptions\nFirst option");
        result.Capability.Should().Be(ComposeCapability.Unspecified);
    }

    [Fact]
    public async Task DispatchAsync_UnsupportedProducerDefersTextWithoutProducingOrSending()
    {
        var producer = Substitute.For<IChannelNativeMessageProducer>();
        producer.Channel.Returns(ChannelId.From("lark"));
        producer.Evaluate(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>()).Returns(ComposeCapability.Unsupported);
        var fixture = Create(producer);

        var result = await fixture.Dispatcher.DispatchAsync(ChannelId.From("lark"),
            "inbound-alpha", "reply-token", new MessageContent { Text = "fallback text" },
            new ComposeContext(), deferTextDelivery: true);

        AssertDeferred(result, fixture.Handler, "fallback text");
        result.Capability.Should().Be(ComposeCapability.Unsupported);
        producer.DidNotReceive().Produce(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>());
    }

    [Fact]
    public async Task DispatchAsync_TelegramNoninteractiveTextDefersBeforeEscapingAndTruncation()
    {
        var fixture = Create(new TelegramChannelNativeMessageProducer(new TelegramMessageComposer()));
        var text = new string('*', 4200) + " last_word😀";
        var intent = new MessageContent { Text = text };
        intent.Cards.Add(new CardBlock { Title = "Additional information", Text = "Keep this tail" });

        var result = await fixture.Dispatcher.DispatchAsync(ChannelId.From("telegram"),
            "inbound-alpha", "reply-token", intent, new ComposeContext(), deferTextDelivery: true);

        AssertDeferred(result, fixture.Handler, text + "\nAdditional information\nKeep this tail");
        result.DeferredText.Should().NotContain("\\*");
        result.DeferredText.Should().EndWith("Keep this tail");
    }

    [Fact]
    public async Task DispatchAsync_RealInteractiveLarkProducerStillSendsWithDeferralEnabled()
    {
        var fixture = Create(new LarkChannelNativeMessageProducer(new LarkMessageComposer()));
        var intent = new MessageContent { Text = "Choose an action" };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button, ActionId = "approve-alpha", Label = "Approve", IsPrimary = true,
        });

        var result = await fixture.Dispatcher.DispatchAsync(ChannelId.From("lark"),
            "inbound-alpha", "reply-token", intent, new ComposeContext(), deferTextDelivery: true);

        result.Succeeded.Should().BeTrue();
        result.DeferredText.Should().BeNull();
        result.FellBackToText.Should().BeFalse();
        result.MessageId.Should().Be("reply-alpha");
        fixture.Handler.Count.Should().Be(1);
        fixture.Handler.Path.Should().Be("/api/v1/channel-relay/reply");
        fixture.Handler.Authorization.Should().Be("Bearer reply-token");
        using var payload = JsonDocument.Parse(fixture.Handler.Body!);
        payload.RootElement.GetProperty("message_id").GetString().Should().Be("inbound-alpha");
        var card = payload.RootElement.GetProperty("reply").GetProperty("metadata").GetProperty("card");
        card.GetProperty("schema").GetString().Should().Be("2.0");
        card.GetProperty("body").GetProperty("elements").GetRawText().Should().Contain("Approve");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAsync_DefaultArgumentKeepsImmediateTextDelivery(bool useTelegramProducer)
    {
        var fixture = useTelegramProducer
            ? Create(new TelegramChannelNativeMessageProducer(new TelegramMessageComposer()))
            : Create();

        // Deliberately omit deferTextDelivery: ordinary callers retain their existing behavior.
        var result = await fixture.Dispatcher.DispatchAsync(ChannelId.From("telegram"),
            "inbound-alpha", "reply-token", new MessageContent { Text = "one_two" }, new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.DeferredText.Should().BeNull();
        result.FellBackToText.Should().BeTrue();
        fixture.Handler.Count.Should().Be(1);
        using var payload = JsonDocument.Parse(fixture.Handler.Body!);
        payload.RootElement.GetProperty("reply").GetProperty("text").GetString()
            .Should().Be(useTelegramProducer ? "one\\_two" : "one_two");
    }

    private static void AssertDeferred(InteractiveReplyDispatchResult result, RecordingHandler handler, string text)
    {
        result.Succeeded.Should().BeFalse();
        result.FellBackToText.Should().BeTrue();
        result.DeferredText.Should().Be(text);
        result.MessageId.Should().BeNull();
        result.PlatformMessageId.Should().BeNull();
        result.Detail.Should().BeNull();
        handler.Count.Should().Be(0);
        handler.Body.Should().BeNull();
    }

    private static Fixture Create(params IChannelNativeMessageProducer[] producers)
    {
        var handler = new RecordingHandler();
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler), NullLogger<NyxIdApiClient>.Instance);
        var registry = new ChannelMessageComposerRegistry([], producers);
        return new Fixture(new NyxIdRelayInteractiveReplyDispatcher(registry, client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance), handler);
    }

    private sealed record Fixture(NyxIdRelayInteractiveReplyDispatcher Dispatcher, RecordingHandler Handler);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message_id\":\"reply-alpha\",\"platform_message_id\":\"platform-alpha\"}",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
