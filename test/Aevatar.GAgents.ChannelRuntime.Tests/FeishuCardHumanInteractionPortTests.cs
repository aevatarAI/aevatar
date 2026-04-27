using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class FeishuCardHumanInteractionPortTests
{
    [Fact]
    public async Task DeliverSuspensionAsync_ShouldSendInteractiveCardThroughNyxProxy()
    {
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
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
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, new LarkMessageComposer(), NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverSuspensionAsync(BuildApprovalRequest(), "agent-1", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("https://nyx.example.com/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id");

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_chat_1");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");

        using var content = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        content.RootElement.GetProperty("schema").GetString().Should().Be("2.0");
        content.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Approval required.");
        var bodyElements = content.RootElement.GetProperty("body").GetProperty("elements");
        bodyElements.GetArrayLength().Should().BeGreaterThan(1);
        bodyElements[1].GetProperty("tag").GetString().Should().Be("form");
        var formElements = bodyElements[1].GetProperty("elements");
        var approveButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Approve");
        var rejectButton = formElements
            .EnumerateArray()
            .First(e => e.GetProperty("tag").GetString() == "button" &&
                        e.GetProperty("text").GetProperty("content").GetString() == "Reject");
        approveButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Approve");
        rejectButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Reject");
        approveButton.GetProperty("behaviors")[0].GetProperty("value").GetProperty("actor_id").GetString()
            .Should().Be("workflow-actor-1");
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat_ViaHttp400Envelope()
    {
        // Reviewer (PR #412 second-pass review): the 230002→fallback retry was added to
        // `FeishuCardHumanInteractionPort.SendMessageAsync` but coverage for the catalog-backed
        // path lives only in `SkillRunnerGAgentTests`. If `UserAgentCatalogProjector.Materialize`
        // / `UserAgentCatalogQueryPort.ToEntry` ever drop the new
        // `LarkReceiveIdFallback` / `LarkReceiveIdTypeFallback` mirror, the existing port tests
        // (which only assert primary success) would still pass while production cards stop
        // delivering on cross-app same-tenant DMs. Pin: catalog entry exposes a chat_id primary
        // + union_id fallback; primary is rejected with the real wrapped envelope shape that
        // `NyxIdApiClient.SendAsync` produces for HTTP-non-2xx responses; the port retries once
        // with the fallback typed pair and the second POST carries `receive_id_type=union_id`
        // and `receive_id=on_*`.
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        registry.GetAsync("agent-fb", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-fb",
                Platform = "lark",
                ConversationId = "oc_dm_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key-fb",
                TemplateName = "social_media",
                LarkReceiveId = "oc_dm_chat_1",
                LarkReceiveIdType = "chat_id",
                LarkReceiveIdFallback = "on_user_1",
                LarkReceiveIdTypeFallback = "union_id",
            }));

        var handler = new SequencedRecordingHandler(
            """{"error": true, "status": 400, "body": "{\"code\":230002,\"msg\":\"Bot is not in the chat\"}"}""",
            """{"data":{"message_id":"om_fb"}}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, new LarkMessageComposer(), NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverSuspensionAsync(BuildApprovalRequest(), "agent-fb", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");

        using var primaryBody = JsonDocument.Parse(handler.Bodies[0]!);
        primaryBody.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_dm_chat_1");

        using var fallbackBody = JsonDocument.Parse(handler.Bodies[1]!);
        fallbackBody.RootElement.GetProperty("receive_id").GetString().Should().Be("on_user_1");
        fallbackBody.RootElement.GetProperty("msg_type").GetString().Should().Be("interactive");
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrow_WhenTargetMissing()
    {
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        registry.GetAsync("missing-agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(null));

        var port = new FeishuCardHumanInteractionPort(
            registry,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            new LarkMessageComposer(),
            NullLogger<FeishuCardHumanInteractionPort>.Instance);

        var act = () => port.DeliverSuspensionAsync(BuildApprovalRequest(), "missing-agent", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*delivery target not found*");
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrow_WhenPlatformUnsupported()
    {
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
        registry.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "agent-2",
                Platform = "telegram",
            }));

        var port = new FeishuCardHumanInteractionPort(
            registry,
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            new LarkMessageComposer(),
            NullLogger<FeishuCardHumanInteractionPort>.Instance);

        var act = () => port.DeliverSuspensionAsync(BuildApprovalRequest(), "agent-2", CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Unsupported human interaction platform*");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldSendResolutionTextThenApprovedContent()
    {
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
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
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, new LarkMessageComposer(), NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor-1",
                RunId = "run-2",
                StepId = "approval-2",
                Approved = true,
                Feedback = "Looks good",
                ResolvedContent = "Launch day update.",
            },
            "agent-1",
            CancellationToken.None);

        handler.Bodies.Should().HaveCount(2);

        using var summaryBody = JsonDocument.Parse(handler.Bodies[0]);
        summaryBody.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        using var summaryContent = JsonDocument.Parse(summaryBody.RootElement.GetProperty("content").GetString()!);
        var summaryText = summaryContent.RootElement.GetProperty("text").GetString();
        summaryText.Should().Contain("Approval recorded.");
        summaryText.Should().Contain("Run ID: run-2");
        summaryText.Should().Contain("Feedback: Looks good");

        using var textBody = JsonDocument.Parse(handler.Bodies[1]);
        textBody.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        using var textContent = JsonDocument.Parse(textBody.RootElement.GetProperty("content").GetString()!);
        textContent.RootElement.GetProperty("text").GetString().Should().Be("Launch day update.");
    }

    [Fact]
    public async Task DeliverApprovalResolutionAsync_ShouldIncludeTextRerunInstructions_ForRejectedSocialMedia()
    {
        var registry = Substitute.For<IUserAgentCatalogRuntimeQueryPort>();
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
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var port = new FeishuCardHumanInteractionPort(registry, nyxClient, new LarkMessageComposer(), NullLogger<FeishuCardHumanInteractionPort>.Instance);

        await port.DeliverApprovalResolutionAsync(
            new HumanApprovalResolution
            {
                ActorId = "workflow-actor-1",
                RunId = "run-3",
                StepId = "approval-3",
                Approved = false,
                Feedback = "Need stronger hook",
            },
            "agent-1",
            CancellationToken.None);

        handler.Bodies.Should().HaveCount(1);
        using var body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        using var content = JsonDocument.Parse(body.RootElement.GetProperty("content").GetString()!);
        var text = content.RootElement.GetProperty("text").GetString();
        text.Should().Contain("Rejection recorded.");
        text.Should().Contain("Feedback: Need stronger hook");
        text.Should().Contain("/run-agent agent-1");
        text.Should().Contain("/agents");
    }

    [Fact]
    public void BuildSuspensionText_ShouldRenderApprovalCommands_ForHumanApproval()
    {
        var text = FeishuCardHumanInteractionPort.BuildSuspensionText(BuildApprovalRequest());

        text.Should().Contain("Approval required.");
        text.Should().Contain("Current content:");
        text.Should().Contain("/approve actor_id=workflow-actor-1 run_id=run-1 step_id=approval-1");
        text.Should().Contain("edited_content=\"final approved content\"");
        text.Should().Contain("/reject actor_id=workflow-actor-1 run_id=run-1 step_id=approval-1 feedback=\"what should change\"");
    }

    [Fact]
    public void BuildSuspensionText_ShouldRenderSubmitCommand_ForHumanInput()
    {
        var text = FeishuCardHumanInteractionPort.BuildSuspensionText(new HumanInteractionRequest
        {
            ActorId = "workflow-actor-2",
            RunId = "run-2",
            StepId = "input-1",
            SuspensionType = "human_input",
            Prompt = "Provide more context",
            Content = "Current summary",
            Options = ["submit"],
        });

        text.Should().Contain("Input required.");
        text.Should().Contain("/submit actor_id=workflow-actor-2 run_id=run-2 step_id=input-1 user_input=\"your response here\"");
    }

    [Fact]
    public void BuildCardJson_ShouldRenderSubmitForm_ForHumanInput()
    {
        var cardJson = FeishuCardHumanInteractionPort.BuildCardJson(new HumanInteractionRequest
        {
            ActorId = "workflow-actor-2",
            RunId = "run-2",
            StepId = "input-1",
            SuspensionType = "human_input",
            Prompt = "Provide more context",
            Content = "Current summary",
            Options = ["submit"],
        });

        using var document = JsonDocument.Parse(cardJson);
        document.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Input required.");
        var form = document.RootElement.GetProperty("body").GetProperty("elements")[1];
        form.GetProperty("tag").GetString().Should().Be("form");
        var formElements = form.GetProperty("elements");
        document.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString()
            .Should().Contain("/submit actor_id=workflow-actor-2 run_id=run-2 step_id=input-1");
        var input = formElements
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "input");
        input.GetProperty("name").GetString().Should().Be("user_input");
        input.TryGetProperty("label", out _).Should().BeFalse();
        var submitButton = formElements
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "button");
        submitButton.GetProperty("text").GetProperty("content").GetString().Should().Be("Submit");
        submitButton.GetProperty("behaviors")[0].GetProperty("value").GetProperty("run_id").GetString()
            .Should().Be("run-2");
    }

    [Fact]
    public void BuildCardJson_ShouldRenderFallbackCommands_ForHumanApproval()
    {
        var cardJson = FeishuCardHumanInteractionPort.BuildCardJson(BuildApprovalRequest());

        using var document = JsonDocument.Parse(cardJson);
        var markdown = document.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("content").GetString();
        markdown.Should().Contain("Actor: `workflow-actor-1`");
        markdown.Should().Contain("/approve actor_id=workflow-actor-1 run_id=run-1 step_id=approval-1");
        markdown.Should().Contain("/reject actor_id=workflow-actor-1 run_id=run-1 step_id=approval-1");
    }

    [Fact]
    public void BuildApprovalResolutionText_ShouldRenderApprovedSummary()
    {
        var text = FeishuCardHumanInteractionPort.BuildApprovalResolutionText(new HumanApprovalResolution
        {
            ActorId = "workflow-actor-3",
            RunId = "run-3",
            StepId = "approval-3",
            Approved = true,
        });

        text.Should().Contain("Approval recorded.");
        text.Should().Contain("Run ID: run-3");
        text.Should().Contain("Step ID: approval-3");
    }

    private static HumanInteractionRequest BuildApprovalRequest() => new()
    {
        ActorId = "workflow-actor-1",
        RunId = "run-1",
        StepId = "approval-1",
        SuspensionType = "human_approval",
        Prompt = "Need approval",
        Content = "Please confirm the publication.",
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

    /// <summary>
    /// Returns a different response per request in the order given so the primary→fallback
    /// retry can be exercised. Records every request + body so tests can assert the order
    /// of <c>receive_id_type</c> and the fallback <c>receive_id</c> on the second POST.
    /// </summary>
    private sealed class SequencedRecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        public SequencedRecordingHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
