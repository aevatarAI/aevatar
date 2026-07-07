using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayChannelInteractionNotificationPortTests
{
    [Fact]
    public async Task DeliverAsync_WhenLarkTarget_ShouldSendComposedInteractiveCardThroughNyxProxy()
    {
        var registry = BuildRegistry(BuildTarget("agent-lark-1", "lark", "oc_chat_1"));
        var handler = new RecordingHandler("""{"code":0,"data":{"message_id":"om_1"}}""");
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            registry,
            CreateNyxClient(handler),
            CreateLarkRelayDispatcher(handler),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        await port.DeliverAsync(BuildApprovalRequest("agent-lark-1"), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_chat_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
        var content = body.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        content.Should().Contain("\"schema\":\"2.0\"");
        content.Should().Contain("\"actor_id\":\"workflow-actor-1\"");
        content.Should().Contain("\"approved\":true");
    }

    [Fact]
    public async Task DeliverAsync_WhenTelegramTarget_ShouldSendTextAndReplyMarkupThroughSameGenericPort()
    {
        var registry = BuildRegistry(BuildTarget("agent-telegram-1", "telegram", "12345"));
        var handler = new RecordingHandler("""{"ok":true,"result":{"message_id":7}}""");
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            registry,
            CreateNyxClient(handler),
            Substitute.For<ILarkOutboundRelayDispatcher>(),
            [new TelegramChannelNativeMessageProducer(new TelegramMessageComposer())],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        await port.DeliverAsync(BuildApprovalRequest("agent-telegram-1"), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-telegram-bot/sendMessage");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("chat_id").GetString().Should().Be("12345");
        body.RootElement.GetProperty("parse_mode").GetString().Should().Be("Markdown");
        body.RootElement.GetProperty("text").GetString().Should().Contain("Approval required");
        body.RootElement.GetProperty("text").GetString().Should().Contain("Approve");
        body.RootElement.GetProperty("text").GetString().Should().Contain("Reject");
        body.RootElement.TryGetProperty("reply_markup", out _).Should().BeFalse(
            "Telegram cannot safely fit typed workflow resume identity in callback data, so approval delivery degrades to text");
    }

    [Fact]
    public async Task DeliverAsync_WhenTelegramProxyReturnsOkFalse_ShouldFailDelivery()
    {
        var registry = BuildRegistry(BuildTarget("agent-telegram-1", "telegram", "12345"));
        var handler = new RecordingHandler("""{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""");
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            registry,
            CreateNyxClient(handler),
            Substitute.For<ILarkOutboundRelayDispatcher>(),
            [new TelegramChannelNativeMessageProducer(new TelegramMessageComposer())],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        Func<Task> act = () => port.DeliverAsync(BuildApprovalRequest("agent-telegram-1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*telegram_code=403*Forbidden: bot was blocked by the user*");
    }

    [Fact]
    public async Task DeliverAsync_WhenTemplateSpecTargetsLark_ShouldSendSafeFallbackCardThroughGenericRelay()
    {
        var registry = BuildRegistry(BuildTarget("agent-lark-template-1", "lark", "oc_chat_1"));
        var handler = new RecordingHandler("""{"code":0,"data":{"message_id":"om_1"}}""");
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            registry,
            CreateNyxClient(handler),
            CreateLarkRelayDispatcher(handler),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["run"] = "run-1";
        template.TemplateVariable["title"] = "Deploy";

        await port.DeliverAsync(new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-template",
            DeliveryTargetId = "agent-lark-template-1",
            InteractionTemplateSpec = template,
        }, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
        var content = body.RootElement.GetProperty("content").GetString();
        content.Should().NotBeNull();
        content.Should().Contain("Interaction notification template: tpl-1");
        content.Should().Contain("Template ID: tpl-1");
        content.Should().Contain("Deploy");
        content.Should().Contain("run-1");
        content.Should().NotContain("\"type\":\"template\"",
            "the generic relay path intentionally keeps templates channel-neutral instead of depending on Lark-native templates");
    }

    [Fact]
    public async Task DeliverAsync_WhenNoProducerRegistered_ShouldReturnExplicitUnsupportedResult()
    {
        var registry = BuildRegistry(BuildTarget("agent-discord-1", "discord", "channel-1"));
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            registry,
            CreateNyxClient(new RecordingHandler("""{"ok":true}""")),
            Substitute.For<ILarkOutboundRelayDispatcher>(),
            [],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        Func<Task> act = () => port.DeliverAsync(BuildApprovalRequest("agent-discord-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*No channel message producer is registered for platform: discord*");
    }

    private static ChannelInteractionNotificationRequest BuildApprovalRequest(string deliveryTargetId) =>
        new()
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "step-1",
            DeliveryTargetId = deliveryTargetId,
            InteractionSpec = new InteractionSpec
            {
                Title = "Approval required",
                Body = "Approve deployment?",
                Actions =
                {
                    new InteractionAction
                    {
                        Kind = InteractionActionKind.FormSubmit,
                        ActionId = "approve",
                        Label = "Approve",
                        ApprovalDecision = InteractionApprovalDecision.Approve,
                    },
                    new InteractionAction
                    {
                        Kind = InteractionActionKind.FormSubmit,
                        ActionId = "reject",
                        Label = "Reject",
                        ApprovalDecision = InteractionApprovalDecision.Reject,
                    },
                },
            },
        };

    private static IUserAgentDeliveryTargetReader BuildRegistry(UserAgentDeliveryTarget target)
    {
        var registry = Substitute.For<IUserAgentDeliveryTargetReader>();
        registry.GetAsync(target.AgentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentDeliveryTarget?>(target));
        return registry;
    }

    private static UserAgentDeliveryTarget BuildTarget(
        string deliveryTargetId,
        string platform,
        string conversationId) =>
        new(
            AgentId: deliveryTargetId,
            Platform: platform,
            ConversationId: conversationId,
            NyxProviderSlug: platform == "telegram" ? "api-telegram-bot" : "api-lark-bot",
            NyxApiKey: "nyx-api-key-1",
            LarkReceiveId: platform == "lark" ? conversationId : string.Empty,
            LarkReceiveIdType: platform == "lark" ? "chat_id" : string.Empty,
            LarkReceiveIdFallback: string.Empty,
            LarkReceiveIdTypeFallback: string.Empty,
            OutputFormat: SkillRunnerOutputFormat.Auto,
            TemplateName: string.Empty,
            AgentType: string.Empty);

    private static NyxIdApiClient CreateNyxClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

    private static ILarkOutboundRelayDispatcher CreateLarkRelayDispatcher(HttpMessageHandler handler) =>
        new LarkOutboundRelayDispatcher(new LarkOutboundDispatcher(CreateNyxClient(handler), NullLogger.Instance));

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
