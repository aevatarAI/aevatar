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
        var registry = Substitute.For<IAgentRegistryQueryPort>();
        registry.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-api-key-1",
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
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("orange");
        var actionElement = card.RootElement.GetProperty("elements")[1];
        actionElement.GetProperty("tag").GetString().Should().Be("action");
        var approve = actionElement.GetProperty("actions")[0];
        approve.GetProperty("value").GetProperty("actor_id").GetString().Should().Be("workflow-actor-1");
        approve.GetProperty("value").GetProperty("run_id").GetString().Should().Be("run-1");
        approve.GetProperty("value").GetProperty("step_id").GetString().Should().Be("approval-1");
        approve.GetProperty("value").GetProperty("approved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DeliverSuspensionAsync_ShouldThrow_WhenTargetMissing()
    {
        var registry = Substitute.For<IAgentRegistryQueryPort>();
        registry.GetAsync("missing-agent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(null));

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
        var registry = Substitute.For<IAgentRegistryQueryPort>();
        registry.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
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
        card.RootElement.GetProperty("header").GetProperty("template").GetString().Should().Be("blue");
        card.RootElement.GetProperty("elements").GetArrayLength().Should().Be(1);
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
