using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;

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
    public async Task DispatchAsync_lark_action_card_forwards_card_metadata()
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
                Text: "Pick one",
                CardPayload: new { schema = "2.0", body = new { elements = Array.Empty<object>() } },
                MessageType: "interactive",
                Capability: ComposeCapability.Exact));
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);
        var intent = new MessageContent { Text = "Pick one" };
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "approve",
            Label = "Approve",
            IsPrimary = true,
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "reject",
            Label = "Reject",
        });

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("lark"),
            "msg-lark-actions",
            "relay-token",
            intent,
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeFalse();
        result.Capability.Should().Be(ComposeCapability.Exact);
        handler.LastRequestBody.Should().NotBeNull();
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement
            .GetProperty("reply")
            .GetProperty("text")
            .GetString()
            .Should()
            .Be("Pick one");
        document.RootElement
            .GetProperty("reply")
            .GetProperty("metadata")
            .GetProperty("card")
            .GetProperty("schema")
            .GetString()
            .Should()
            .Be("2.0");
    }

    [Fact]
    public async Task DispatchAsync_lark_action_intent_sends_text_fallback_even_when_native_is_text()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { message_id = "mid", platform_message_id = "pmid" }));
        var client = CreateClient(handler);
        var producer = Substitute.For<IChannelNativeMessageProducer>();
        producer.Channel.Returns(ChannelId.From("lark"));
        producer.Evaluate(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>())
            .Returns(ComposeCapability.Degraded);
        producer.Produce(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>())
            .Returns(new ChannelNativeMessage(
                Text: "Pick one",
                CardPayload: null,
                MessageType: "text",
                Capability: ComposeCapability.Degraded));
        var registry = new ChannelMessageComposerRegistry(
            Array.Empty<IMessageComposer>(),
            new[] { producer });
        var dispatcher = new NyxIdRelayInteractiveReplyDispatcher(
            registry,
            client,
            NullLogger<NyxIdRelayInteractiveReplyDispatcher>.Instance);
        var intent = new MessageContent { Text = "Pick one" };
        var card = new CardBlock { Title = "Routing", Text = "Choose a route" };
        card.Fields.Add(new CardField { Title = "Current", Text = "gpt-4.1" });
        intent.Cards.Add(card);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "use-fast",
            Label = "Use fast model",
        });

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("lark"),
            "msg-lark-native-text-actions",
            "relay-token",
            intent,
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeTrue();
        producer.Received(1).Produce(Arg.Any<MessageContent>(), Arg.Any<ComposeContext>());
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement
            .GetProperty("reply")
            .GetProperty("text")
            .GetString()
            .Should()
            .Be("Pick one");
        handler.LastRequestBody.Should().NotContain("metadata");
        handler.LastRequestBody.Should().NotContain("card");
    }

    [Fact]
    public async Task DispatchAsync_without_producer_synthesizes_fallback_text_from_cards()
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

        var intent = new MessageContent();
        var card = new CardBlock { Title = "Summary Report", Text = "3 PRs merged" };
        card.Fields.Add(new CardField { Title = "Commits", Text = "42" });
        intent.Cards.Add(card);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "detail",
            Label = "Open details",
        });

        var result = await dispatcher.DispatchAsync(
            ChannelId.From("telegram"),
            "msg-5",
            "relay-token",
            intent,
            new ComposeContext());

        result.Succeeded.Should().BeTrue();
        result.FellBackToText.Should().BeTrue();
        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestBody!.Should().Contain("Summary Report");
        handler.LastRequestBody.Should().Contain("3 PRs merged");
        handler.LastRequestBody.Should().Contain("Commits: 42");
        handler.LastRequestBody.Should().Contain("Open details");
        handler.LastRequestBody.Should().NotContain("(no content)");
    }

    [Fact]
    public void BuildTextFallback_card_only_intent_flattens_title_text_fields_actions()
    {
        var intent = new MessageContent();
        var card = new CardBlock { Title = "Title", Text = "Body" };
        card.Fields.Add(new CardField { Title = "Key", Text = "Value" });
        card.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "detail",
            Label = "Details",
        });
        intent.Cards.Add(card);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "comment",
            Label = "Comment",
        });

        var fallback = NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(intent);

        fallback.Should().Contain("Title");
        fallback.Should().Contain("Body");
        fallback.Should().Contain("Key: Value");
        fallback.Should().Contain("• Confirm");
        fallback.Should().Contain("• Details");
        fallback.Should().NotContain("Comment", "text input labels should not appear in text fallback");
    }

    [Fact]
    public void BuildTextFallback_existing_text_includes_card_fields_and_non_input_action_labels()
    {
        var intent = new MessageContent { Text = "explicit text" };
        var card = new CardBlock { Title = "card-title", Text = "card-body" };
        card.Fields.Add(new CardField { Title = "Service", Text = "nyxid" });
        intent.Cards.Add(card);
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
        });
        intent.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.TextInput,
            ActionId = "comment",
            Label = "Comment",
        });

        var fallback = NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(intent);

        fallback.Should().Be("explicit text\ncard-title\ncard-body\nService: nyxid\n• Confirm");
        fallback.Should().NotContain("Comment");
    }

    [Fact]
    public void BuildTextFallback_select_action_includes_option_labels()
    {
        var intent = new MessageContent { Text = "Choose a model" };
        var select = new ActionElement
        {
            Kind = ActionElementKind.Select,
            ActionId = "model",
            Label = "Model",
        };
        select.Options.Add(new ActionOption { Label = "GPT-4.1", Value = "gpt-4.1" });
        select.Options.Add(new ActionOption { Label = "Claude Sonnet", Value = "sonnet" });
        intent.Actions.Add(select);

        var fallback = NyxIdRelayInteractiveReplyDispatcher.BuildTextFallback(intent);

        fallback.Should().Be("Choose a model\n• Model\n  - GPT-4.1\n  - Claude Sonnet");
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
