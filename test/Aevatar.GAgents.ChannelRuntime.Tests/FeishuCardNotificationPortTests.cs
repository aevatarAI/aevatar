using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class FeishuCardNotificationPortTests
{
    [Fact]
    public async Task DeliverAsync_WhenInteractionSpecPresent_ShouldSendInteractiveCardThroughDispatcherPath()
    {
        var registry = BuildRegistry("agent-1");
        var handler = new RecordingHandler("""{"data":{"message_id":"om_1"}}""");
        var nyxClient = CreateNyxClient(handler);
        var port = new FeishuCardNotificationPort(
            registry,
            nyxClient,
            new LarkMessageComposer(),
            NullLogger<FeishuCardNotificationPort>.Instance);

        await port.DeliverAsync(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "notify-1",
                DeliveryTargetId = "agent-1",
                InteractionSpec = new InteractionSpec
                {
                    Title = "Status",
                    Body = "Accepted",
                    Actions =
                    {
                        new InteractionAction
                        {
                            Kind = InteractionActionKind.Button,
                            ActionId = "open",
                            Label = "Open",
                        },
                    },
                },
            },
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_chat_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");

        using var content = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        content.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        content.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Status\nAccepted");
    }

    [Fact]
    public void BuildCardJson_WhenTemplateSpecPresent_ShouldRenderLarkTemplateContent()
    {
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["title"] = "Deploy";
        template.TemplateVariable["run"] = "run-1";

        var cardJson = FeishuCardNotificationPort.BuildCardJson(
            new ChannelInteractionNotificationRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "notify-template",
                DeliveryTargetId = "agent-1",
                InteractionTemplateSpec = template,
            });

        using var document = JsonDocument.Parse(cardJson);
        document.RootElement.GetProperty("type").GetString().Should().Be("template");
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("template_id").GetString().Should().Be("tpl-1");
        data.GetProperty("template_variable").GetProperty("title").GetString().Should().Be("Deploy");
        data.GetProperty("template_variable").GetProperty("run").GetString().Should().Be("run-1");
    }

    [Fact]
    public async Task DeliverAsync_ShouldRetryWithFallback_WhenPrimaryRejectedAsBotNotInChat()
    {
        var registry = BuildRegistry("agent-fb", fallback: true);
        var handler = new SequencedRecordingHandler(
            """{"error": true, "status": 400, "body": "{\"code\":230002,\"msg\":\"Bot is not in the chat\"}"}""",
            """{"data":{"message_id":"om_fb"}}""");
        var port = new FeishuCardNotificationPort(
            registry,
            CreateNyxClient(handler),
            new LarkMessageComposer(),
            NullLogger<FeishuCardNotificationPort>.Instance);

        await port.DeliverAsync(BuildTemplateRequest("agent-fb"), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        using var fallbackBody = JsonDocument.Parse(handler.Bodies[1]!);
        fallbackBody.RootElement.GetProperty("receive_id").GetString().Should().Be("on_user_1");
        fallbackBody.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
    }

    [Fact]
    public async Task DeliverAsync_ShouldThrow_WhenTargetMissingOrPlatformUnsupported()
    {
        var missingRegistry = Substitute.For<IUserAgentDeliveryTargetReader>();
        missingRegistry.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentDeliveryTarget?>(null));
        var unsupportedRegistry = Substitute.For<IUserAgentDeliveryTargetReader>();
        unsupportedRegistry.GetAsync("agent-telegram", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentDeliveryTarget?>(new UserAgentDeliveryTarget(
                AgentId: "agent-telegram",
                Platform: "telegram",
                ConversationId: string.Empty,
                NyxProviderSlug: string.Empty,
                NyxApiKey: string.Empty,
                LarkReceiveId: string.Empty,
                LarkReceiveIdType: string.Empty,
                LarkReceiveIdFallback: string.Empty,
                LarkReceiveIdTypeFallback: string.Empty,
                OutputFormat: SkillRunnerOutputFormat.Auto,
                TemplateName: string.Empty,
                AgentType: string.Empty)));
        var missingPort = CreatePort(missingRegistry);
        var unsupportedPort = CreatePort(unsupportedRegistry);

        Func<Task> missingAct = () => missingPort.DeliverAsync(BuildTemplateRequest("missing"), CancellationToken.None);
        Func<Task> unsupportedAct = () => unsupportedPort.DeliverAsync(BuildTemplateRequest("agent-telegram"), CancellationToken.None);

        await missingAct.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*delivery target not found*");
        await unsupportedAct.Should()
            .ThrowAsync<NotSupportedException>()
            .WithMessage("*Unsupported interaction notification platform*");
    }

    [Fact]
    public async Task BuildCardJson_ShouldRejectMissingOrDoublePayload()
    {
        var empty = new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-1",
            DeliveryTargetId = "agent-1",
        };
        var both = empty with
        {
            InteractionSpec = new InteractionSpec { Title = "Status" },
            InteractionTemplateSpec = new InteractionTemplateSpec { TemplateId = "tpl-1" },
        };

        Action emptyAct = () => FeishuCardNotificationPort.BuildCardJson(empty);
        Func<Task> bothAct = () => CreatePort(BuildRegistry("agent-1")).DeliverAsync(both, CancellationToken.None);

        emptyAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*payload is required*");
        await bothAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one typed payload*");
    }

    private static FeishuCardNotificationPort CreatePort(IUserAgentDeliveryTargetReader registry) =>
        new(
            registry,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            new LarkMessageComposer(),
            NullLogger<FeishuCardNotificationPort>.Instance);

    private static IUserAgentDeliveryTargetReader BuildRegistry(string deliveryTargetId, bool fallback = false)
    {
        var registry = Substitute.For<IUserAgentDeliveryTargetReader>();
        registry.GetAsync(deliveryTargetId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentDeliveryTarget?>(new UserAgentDeliveryTarget(
                AgentId: deliveryTargetId,
                Platform: "lark",
                ConversationId: fallback ? "oc_dm_chat_1" : "oc_chat_1",
                NyxProviderSlug: "api-lark-bot",
                NyxApiKey: "nyx-api-key-1",
                LarkReceiveId: fallback ? "oc_dm_chat_1" : string.Empty,
                LarkReceiveIdType: fallback ? "chat_id" : string.Empty,
                LarkReceiveIdFallback: fallback ? "on_user_1" : string.Empty,
                LarkReceiveIdTypeFallback: fallback ? "union_id" : string.Empty,
                OutputFormat: SkillRunnerOutputFormat.Auto,
                TemplateName: "social_media",
                AgentType: string.Empty)));
        return registry;
    }

    private static ChannelInteractionNotificationRequest BuildTemplateRequest(string deliveryTargetId)
    {
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["title"] = "Deploy";
        return new ChannelInteractionNotificationRequest
        {
            ActorId = "workflow-actor-1",
            RunId = "run-1",
            StepId = "notify-template",
            DeliveryTargetId = deliveryTargetId,
            InteractionTemplateSpec = template,
        };
    }

    private static NyxIdApiClient CreateNyxClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SequencedRecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        public SequencedRecordingHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var body = _responses.Count > 0 ? _responses.Dequeue() : """{"data":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
