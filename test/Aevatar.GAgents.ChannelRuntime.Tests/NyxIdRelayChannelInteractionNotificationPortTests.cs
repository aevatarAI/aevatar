using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
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
    public async Task RemoteApprovalNotifyAsync_ShouldRequireExplicitDeliveryTargetBeforeLookup()
    {
        var registry = Substitute.For<IUserAgentDeliveryTargetReader>();
        var port = new NyxIdRelayRemoteToolApprovalNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            [new LarkChannelNativeMessageSender(CreateLarkOutboundDispatcher(new RecordingHandler("""{"code":0,"data":{"message_id":"om_unused"}}""")))],
            [new LarkChannelNativeDeliveryTargetAdapter()],
            NullLogger<NyxIdRelayRemoteToolApprovalNotificationPort>.Instance);

        Func<Task> act = () => port.NotifyAsync(BuildRemoteApprovalNotification(null), CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit delivery target id*");
        await registry.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoteApprovalNotifyAsync_WhenLarkTarget_ShouldSendInteractiveCardThroughNyxProxy()
    {
        var registry = BuildRegistry(BuildTarget(
            "agent-remote-1",
            "lark",
            "legacy-conversation",
            larkReceiveId: "oc_chat_1"));
        var handler = new RecordingHandler("""{"code":0,"data":{"message_id":"om_remote_1"}}""");
        var port = new NyxIdRelayRemoteToolApprovalNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            [new LarkChannelNativeMessageSender(CreateLarkOutboundDispatcher(handler))],
            [new LarkChannelNativeDeliveryTargetAdapter()],
            NullLogger<NyxIdRelayRemoteToolApprovalNotificationPort>.Instance);

        await port.NotifyAsync(BuildRemoteApprovalNotification("agent-remote-1"), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_chat_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
        using var content = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        var elements = content.RootElement.GetProperty("body").GetProperty("elements");
        var approveValue = elements[1].GetProperty("behaviors")[0].GetProperty("value");
        approveValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("nyx-remote-1");
        approveValue.GetProperty("nyxid_approval_approved").GetBoolean().Should().BeTrue();
        var rejectValue = elements[2].GetProperty("behaviors")[0].GetProperty("value");
        rejectValue.GetProperty("nyxid_approval_request_id").GetString().Should().Be("nyx-remote-1");
        rejectValue.GetProperty("nyxid_approval_approved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RemoteApprovalNotifyAsync_WhenTelegramTarget_ShouldFailClosedBeforeNativeCompositionOrDispatch()
    {
        var registry = BuildRegistry(BuildTarget("agent-telegram-1", "telegram", "12345"));
        var producer = new CountingNativeMessageProducer("telegram");
        var sender = new CountingNativeMessageSender("telegram");
        var port = new NyxIdRelayRemoteToolApprovalNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [producer],
            [sender],
            [],
            NullLogger<NyxIdRelayRemoteToolApprovalNotificationPort>.Instance);

        Func<Task> act = () => port.NotifyAsync(BuildRemoteApprovalNotification("agent-telegram-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*supported only for Lark delivery targets*telegram*");
        producer.EvaluateCallCount.Should().Be(0);
        producer.ProduceCallCount.Should().Be(0);
        sender.SendCallCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoteApprovalNotifyAsync_WhenNonLarkTarget_ShouldFailClosedBeforeNativeCompositionOrDispatch()
    {
        var registry = BuildRegistry(BuildTarget("agent-discord-1", "discord", "channel-1"));
        var producer = new CountingNativeMessageProducer("discord");
        var sender = new CountingNativeMessageSender("discord");
        var port = new NyxIdRelayRemoteToolApprovalNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [producer],
            [sender],
            [],
            NullLogger<NyxIdRelayRemoteToolApprovalNotificationPort>.Instance);

        Func<Task> act = () => port.NotifyAsync(BuildRemoteApprovalNotification("agent-discord-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*supported only for Lark delivery targets*discord*");
        producer.EvaluateCallCount.Should().Be(0);
        producer.ProduceCallCount.Should().Be(0);
        sender.SendCallCount.Should().Be(0);
    }

    [Fact]
    public async Task DeliverAsync_WhenLarkTarget_ShouldSendComposedInteractiveCardThroughNyxProxy()
    {
        var registry = BuildRegistry(BuildTarget(
            "agent-lark-1",
            "lark",
            "legacy-conversation",
            larkReceiveId: "oc_chat_1"));
        var handler = new RecordingHandler("""{"code":0,"data":{"message_id":"om_1"}}""");
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            [new LarkChannelNativeMessageSender(CreateLarkOutboundDispatcher(handler))],
            [new LarkChannelNativeDeliveryTargetAdapter()],
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
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new TelegramChannelNativeMessageProducer(new TelegramMessageComposer())],
            [new TelegramChannelNativeMessageSender(CreateNyxClient(handler))],
            [],
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
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new TelegramChannelNativeMessageProducer(new TelegramMessageComposer())],
            [new TelegramChannelNativeMessageSender(CreateNyxClient(handler))],
            [],
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
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new LarkChannelNativeMessageProducer(new LarkMessageComposer())],
            [new LarkChannelNativeMessageSender(CreateLarkOutboundDispatcher(handler))],
            [new LarkChannelNativeDeliveryTargetAdapter()],
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
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [],
            [],
            [],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        Func<Task> act = () => port.DeliverAsync(BuildApprovalRequest("agent-discord-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*No channel message producer is registered for platform: discord*");
    }

    [Fact]
    public async Task DeliverAsync_WhenNoSenderRegistered_ShouldReturnExplicitUnsupportedResult()
    {
        var registry = BuildRegistry(BuildTarget("agent-telegram-1", "telegram", "12345"));
        var port = new NyxIdRelayChannelInteractionNotificationPort(
            new ChannelDeliveryTargetResolver(registry, NullLogger<ChannelDeliveryTargetResolver>.Instance),
            [new TelegramChannelNativeMessageProducer(new TelegramMessageComposer())],
            [],
            [],
            NullLogger<NyxIdRelayChannelInteractionNotificationPort>.Instance);

        Func<Task> act = () => port.DeliverAsync(BuildApprovalRequest("agent-telegram-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*No channel message sender is registered for platform: telegram*");
    }

    [Fact]
    public void ScheduledDeliveryTarget_ShouldNotImplementLarkRouteContract()
    {
        typeof(ILarkChannelNativeDeliveryRoute)
            .IsAssignableFrom(typeof(UserAgentDeliveryTarget))
            .Should()
            .BeFalse("platform route contracts must be adapted inside the Lark boundary");
    }

    [Fact]
    public void ChannelDeliveryTargetResolver_ShouldNotExposeLarkShapedTargetMembers()
    {
        var memberNames = typeof(ChannelDeliveryTargetResolver)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(static member => member.Name)
            .Concat(typeof(ChannelDeliveryTargetResolver)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(static type => type
                    .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Select(static member => member.Name)
                    .Append(type.Name)))
            .ToArray();

        memberNames.Should().NotContain(static name =>
                name.Contains("Lark", StringComparison.Ordinal) ||
                name.Contains("RoutedChannelNativeDeliveryTarget", StringComparison.Ordinal),
            "Lark receive target construction belongs to the Lark platform adapter");
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

    private static RemoteToolApprovalNotification BuildRemoteApprovalNotification(string? deliveryTargetId) =>
        new(
            new RemoteToolApprovalRequest(
                "local-req-1",
                "dangerous_tool",
                "call-1",
                """{"path":"/prod"}""",
                ToolApprovalMode.Auto,
                IsDestructive: true),
            new RemoteToolApprovalSubmission(
                "nyx-remote-1",
                DateTimeOffset.Parse("2026-06-11T10:00:00Z")),
            AgentToolExecutionContext.Empty with
            {
                Channel = new AgentToolChannelContext(
                    "lark",
                    "sender-1",
                    "scope-1",
                    "msg-1",
                    "om_1",
                    deliveryTargetId),
            });

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
        string conversationId,
        string? larkReceiveId = null) =>
        new(
            AgentId: deliveryTargetId,
            Platform: platform,
            ConversationId: conversationId,
            NyxProviderSlug: platform == "telegram" ? "api-telegram-bot" : "api-lark-bot",
            NyxApiKey: "nyx-api-key-1",
            ChannelAddress: new Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddress(
                platform,
                platform == "telegram" ? "api-telegram-bot" : "api-lark-bot",
                conversationId,
                new Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddressEndpoint(
                    platform == "lark" ? larkReceiveId ?? conversationId : conversationId,
                    platform == "lark" ? "chat_id" : string.Empty)),
            OutputFormat: ScheduledAgentOutputFormat.Auto,
            TemplateName: string.Empty,
            AgentType: string.Empty);

    private static NyxIdApiClient CreateNyxClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

    private static ILarkOutboundDispatcher CreateLarkOutboundDispatcher(HttpMessageHandler handler) =>
        new LarkOutboundDispatcher(CreateNyxClient(handler), NullLogger.Instance);

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

    private sealed class CountingNativeMessageProducer(string platform) : IChannelNativeMessageProducer
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);
        public int EvaluateCallCount { get; private set; }
        public int ProduceCallCount { get; private set; }

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context)
        {
            EvaluateCallCount++;
            return ComposeCapability.Exact;
        }

        public ChannelNativeMessage Produce(MessageContent intent, ComposeContext context)
        {
            ProduceCallCount++;
            return new ChannelNativeMessage(
                Text: null,
                CardPayload: new { type = "interactive" },
                MessageType: "interactive",
                ComposeCapability.Exact);
        }
    }

    private sealed class CountingNativeMessageSender(string platform) : IChannelNativeMessageSender
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);
        public int SendCallCount { get; private set; }

        public Task<EmitResult> SendAsync(
            ChannelNativeDeliveryTarget target,
            ChannelNativeMessage message,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            return Task.FromResult(EmitResult.Sent($"test:{platform}:{SendCallCount}"));
        }

        public Task<EmitResult> UpdateAsync(
            ChannelNativeDeliveryTarget target,
            string platformMessageId,
            ChannelNativeMessage message,
            bool isFinal,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmitResult.Sent(platformMessageId, platformMessageId: platformMessageId));
    }
}
