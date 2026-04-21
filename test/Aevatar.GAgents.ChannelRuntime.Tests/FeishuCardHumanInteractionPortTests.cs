using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class FeishuCardHumanInteractionPortTests
{
    [Fact]
    public async Task DeliverSuspensionAsync_ShouldSendInteractiveCardThroughNyxProxy()
    {
        var registry = Substitute.For<IUserAgentCatalogQueryPort>();
        registry.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key-1",
                TemplateName = "social_media",
            }));

        var handler = new RecordingHandler("""{"data":{"message_id":"om_1"}}""");
        var httpClient = new HttpClient(handler);
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverSuspensionAsync(
            new HumanInteractionRequest
            {
                ActorId = "workflow-actor-1",
                RunId = "run-1",
                StepId = "approval-1",
                SuspensionType = "human_approval",
                Prompt = "Need approval",
                Content = "Please confirm the publication.",
                Options = ["approve", "reject"],
            },
            "agent-1",
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("nyx-api-key-1");

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_chat_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");

        using var card = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        card.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("orange");
        var formElement = card.RootElement.GetProperty("body").GetProperty("elements")[1];
        formElement.GetProperty("tag").GetString().Should().Be("form");
        var editedContentInput = formElement.GetProperty("elements")[0];
        editedContentInput.GetProperty("name").GetString().Should().Be("edited_content");

        var feedbackInput = formElement.GetProperty("elements")[1];
        feedbackInput.GetProperty("name").GetString().Should().Be("user_input");

        var approve = formElement.GetProperty("elements")[2];
        approve.GetProperty("tag").GetString().Should().Be("button");
        approve.GetProperty("form_action_type").GetString().Should().Be("submit");
        approve.GetProperty("value").GetProperty("actor_id").GetString().Should().Be("workflow-actor-1");
        approve.GetProperty("value").GetProperty("run_id").GetString().Should().Be("run-1");
        approve.GetProperty("value").GetProperty("step_id").GetString().Should().Be("approval-1");
        approve.GetProperty("value").GetProperty("approved").GetBoolean().Should().BeTrue();

        var reject = formElement.GetProperty("elements")[3];
        reject.GetProperty("tag").GetString().Should().Be("button");
        reject.GetProperty("form_action_type").GetString().Should().Be("submit");
        reject.GetProperty("value").GetProperty("approved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrow_WhenTargetMissing()
    {
        var registry = Substitute.For<IUserAgentCatalogQueryPort>();
        registry.GetAsync("missing-agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(null));

        var port = new FeishuCardHumanInteractionPort(
            registry,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<FeishuCardHumanInteractionPort>.Instance);

        var act = () => port.DeliverSuspensionAsync(BuildRequest(), "missing-agent", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*delivery target not found*");
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrow_WhenPlatformUnsupported()
    {
        var registry = Substitute.For<IUserAgentCatalogQueryPort>();
        registry.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-2",
                Platform = "telegram",
            }));

        var port = new FeishuCardHumanInteractionPort(
            registry,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            NullLogger<FeishuCardHumanInteractionPort>.Instance);

        var act = () => port.DeliverSuspensionAsync(BuildRequest(), "agent-2", CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Unsupported human interaction platform*");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldSendResolutionCardThroughNyxProxy()
    {
        var registry = Substitute.For<IUserAgentCatalogQueryPort>();
        registry.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key-1",
                TemplateName = "social_media",
            }));

        var handler = new RecordingHandler("""{"data":{"message_id":"om_2"}}""");
        var httpClient = new HttpClient(handler);
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor-1",
                RunId = "run-2",
                StepId = "approval-2",
                Approved = true,
                UserInput = "legacy-approved",
                EditedContent = "Launch day update.",
                Feedback = "Looks good",
                ResolvedContent = "Launch day update.",
            },
            "agent-1",
            CancellationToken.None);

        handler.Bodies.Should().HaveCount(2);

        using var cardBody = JsonDocument.Parse(handler.Bodies[0]);
        cardBody.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
        using var card = JsonDocument.Parse(cardBody.RootElement.GetProperty("content").GetString()!);
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("green");
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Approval Recorded");
        card.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()
            .Should().Contain("posted below");

        using var textBody = JsonDocument.Parse(handler.Bodies[1]);
        textBody.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        using var textContent = JsonDocument.Parse(textBody.RootElement.GetProperty("content").GetString()!);
        textContent.RootElement.GetProperty("text").GetString().Should().Be("Launch day update.");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldOnlySendCard_ForRejectedSocialMedia()
    {
        var registry = Substitute.For<IUserAgentCatalogQueryPort>();
        registry.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key-1",
                TemplateName = "social_media",
            }));

        var handler = new RecordingHandler("""{"data":{"message_id":"om_3"}}""");
        var httpClient = new HttpClient(handler);
        var nyxClient = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }, httpClient);
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor-1",
                RunId = "run-3",
                StepId = "approval-3",
                Approved = false,
                UserInput = "legacy-rejected",
                EditedContent = "Rejected draft content",
                Feedback = "Need stronger hook",
                ResolvedContent = "Rejected draft content",
            },
            "agent-1",
            CancellationToken.None);

        handler.Bodies.Should().HaveCount(1);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
        using var card = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("red");
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Rejection Recorded");
        card.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()
            .Should().Contain("Need stronger hook");

        var actionElement = card.RootElement.GetProperty("body").GetProperty("elements")[1];
        actionElement.GetProperty("tag").GetString().Should().Be("action");
        var rerun = actionElement.GetProperty("actions")[0];
        rerun.GetProperty("text").GetProperty("content").GetString().Should().Be("Run Again");
        rerun.GetProperty("value").GetProperty("agent_builder_action").GetString().Should().Be("run_agent");
        rerun.GetProperty("value").GetProperty("agent_id").GetString().Should().Be("agent-1");
        rerun.GetProperty("value").GetProperty("revision_feedback").GetString().Should().Be("Need stronger hook");

        var viewAgents = actionElement.GetProperty("actions")[1];
        viewAgents.GetProperty("text").GetProperty("content").GetString().Should().Be("View Agents");
        viewAgents.GetProperty("value").GetProperty("agent_builder_action").GetString().Should().Be("list_agents");
    }

    [Fact]
    public void BuildCardJson_ShouldOmitActionButtons_ForHumanInput()
    {
        var json = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "workflow-actor-2",
            RunId = "run-2",
            StepId = "input-1",
            SuspensionType = "human_input",
            Prompt = "Provide more context",
            Content = "Current summary",
            Options = ["submit"],
        });

        using var card = JsonDocument.Parse(json);
        card.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("blue");
        card.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void BuildCardJson_ShouldIncludeEditedContentAndFeedbackInputs_ForHumanApproval()
    {
        var json = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "workflow-actor-4",
            RunId = "run-4",
            StepId = "approval-4",
            SuspensionType = "human_approval",
            Prompt = "Need approval",
            Content = "Review this draft",
            Options = ["approve", "reject"],
        });

        using var card = JsonDocument.Parse(json);
        card.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        card.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength().Should().Be(2);
        card.RootElement.GetProperty("body").GetProperty("elements")[1].GetProperty("tag").GetString().Should().Be("form");
        card.RootElement.GetProperty("body").GetProperty("elements")[1].GetProperty("elements")[0].GetProperty("name").GetString()
            .Should().Be("edited_content");
        card.RootElement.GetProperty("body").GetProperty("elements")[1].GetProperty("elements")[1].GetProperty("name").GetString()
            .Should().Be("user_input");
    }

    [Fact]
    public void BuildApprovalResolutionCardJson_ShouldRenderApprovedCard()
    {
        var json = FeishuCardHumanInteractionPort.BuildApprovalResolutionCardJson(new HumanApprovalResolution
        {
            ActorId = "workflow-actor-3",
            RunId = "run-3",
            StepId = "approval-3",
            Approved = true,
        });

        using var card = JsonDocument.Parse(json);
        card.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("green");
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Approval Recorded");
    }

    private static HumanInteractionRequest BuildRequest() => new()
    {
        ActorId = "workflow-actor-1",
        RunId = "run-1",
        StepId = "approval-1",
        SuspensionType = "human_approval",
        Prompt = "Need approval",
        Options = ["approve", "reject"],
    };

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (LastBody != null)
                Bodies.Add(LastBody);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
