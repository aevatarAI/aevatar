using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkOutboundDispatcherTests
{
    private const string OkResponse = """{"code":0,"msg":"success","data":{"message_id":"om_1"}}""";

    [Fact]
    public async Task SendNewMessageAsync_PrimarySuccess_PostsExpectedBody()
    {
        var handler = new SequencedHandler(OkResponse);
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        result.MessageId.Should().Be("om_1");
        result.UsedFallback.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages");
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("oc_primary");
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        body.RootElement.GetProperty("content").GetString().Should().Be("""{"text":"hello"}""");
    }

    [Fact]
    public async Task SendNewMessageAsync_BotNotInChat_RetriesOnceWithFallback()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"code":230002,"msg":"Bot is not in the chat"}"""),
            (HttpStatusCode.OK, """{"code":0,"msg":"success","data":{"message_id":"om_fallback"}}"""));
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(
            CreateRequest(fallback: new LarkReceiveTarget("on_user", "union_id", FellBackToPrefixInference: false)),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        result.MessageId.Should().Be("om_fallback");
        result.UsedFallback.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");

        using var body = JsonDocument.Parse(handler.Bodies[1]!);
        body.RootElement.GetProperty("receive_id").GetString().Should().Be("on_user");
    }

    [Fact]
    public async Task SendNewMessageAsync_NewMessagePost_MessageIdParser_And230002Fallback_AreCentralized()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"code":230002,"msg":"Bot is not in the chat"}"""),
            (HttpStatusCode.OK, """{"code":0,"msg":"success","data":{"message_id":"om_dispatcher_owned"}}"""));
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(
            CreateRequest(fallback: new LarkReceiveTarget("on_fallback_user", "union_id", FellBackToPrefixInference: false)),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        result.MessageId.Should().Be("om_dispatcher_owned");
        result.UsedFallback.Should().BeTrue();
        result.AttemptedTarget.ReceiveId.Should().Be("on_fallback_user");
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath == "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages");
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
    }

    [Fact]
    public async Task SendNewMessageAsync_NonRetryLarkError_DoesNotFallback()
    {
        var handler = new SequencedHandler("""{"code":99992364,"msg":"user id cross tenant"}""");
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(
            CreateRequest(fallback: new LarkReceiveTarget("on_user", "union_id", FellBackToPrefixInference: false)),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().Be(LarkBotErrorCodes.UserIdCrossTenant);
        result.Detail.Should().Contain("user id cross tenant");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SendNewMessageAsync_IncompleteSuccess_ReturnsParserHint()
    {
        var handler = new SequencedHandler("""{"code":0,"msg":"success","data":{}}""");
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().BeNull();
        result.Detail.Should().Be("missing_message_id");
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("", "empty_send_response")]
    [InlineData("""{"code":0,"msg":"success"}""", "missing_data")]
    [InlineData("""{"code":0,"msg":"success","data":{"message_id":"   "}}""", "empty_message_id")]
    [InlineData("""{"code":0,"msg":"success","data":""", "invalid_send_response_json")]
    public async Task SendNewMessageAsync_InvalidSuccessShape_ReturnsParserHint(
        string response,
        string expectedDetail)
    {
        var handler = new SequencedHandler(response);
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().BeNull();
        result.Detail.Should().Be(expectedDetail);
        handler.Requests.Should().ContainSingle();
    }

    public static TheoryData<LarkSendNewMessageRequest, string> InvalidRequests => new()
    {
        { CreateRequest(nyxApiKey: " "), "NyxID API key is required." },
        { CreateRequest(nyxProviderSlug: " "), "NyxID provider slug is required." },
        { CreateRequest(messageType: " "), "Lark message type is required." },
        { CreateRequest(contentJson: " "), "Lark message content JSON is required." },
        { CreateRequest(primaryTarget: new LarkReceiveTarget(" ", "chat_id", FellBackToPrefixInference: false)), "Lark primary receive_id is required." },
        { CreateRequest(primaryTarget: new LarkReceiveTarget("oc_primary", " ", FellBackToPrefixInference: false)), "Lark primary receive_id_type is required." },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task SendNewMessageAsync_InvalidRequest_ThrowsBeforeHttpDispatch(
        LarkSendNewMessageRequest request,
        string expectedMessage)
    {
        var handler = new SequencedHandler(OkResponse);
        var dispatcher = CreateDispatcher(handler);

        var act = () => dispatcher.SendNewMessageAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request")
            .WithMessage(expectedMessage + "*");
        handler.Requests.Should().BeEmpty();
        handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task SendNewMessageAsync_CrossAppError_ReturnsHintCodeWithoutRetry()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"code":99992361,"msg":"open_id cross app"}"""));
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.SendNewMessageAsync(
            CreateRequest(fallback: new LarkReceiveTarget("on_user", "union_id", FellBackToPrefixInference: false)),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().Be(LarkBotErrorCodes.OpenIdCrossApp);
        result.Detail.Should().Contain("open_id cross app");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateMessageAsync_ShouldPutMessageEdit()
    {
        var handler = new SequencedHandler(OkResponse);
        var dispatcher = CreateDispatcher(handler);

        var result = await dispatcher.UpdateMessageAsync(
            new LarkUpdateMessageRequest(
                "nyx-api-key",
                "api-lark-bot",
                "om_stream",
                "text",
                """{"text":"hello updated"}"""),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        result.MessageId.Should().Be("om_stream");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Method.Should().Be("PUT");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_stream");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        body.RootElement.GetProperty("content").GetString().Should().Be("""{"text":"hello updated"}""");
    }

    [Fact]
    public void LarkSendNewMessageRequest_UsesNyxApiKeySemanticName()
    {
        var request = CreateRequest();

        request.NyxApiKey.Should().Be("nyx-api-key");
    }

    private static LarkOutboundDispatcher CreateDispatcher(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new LarkOutboundDispatcher(client, NullLogger<LarkOutboundDispatcher>.Instance);
    }

    private static LarkSendNewMessageRequest CreateRequest(
        LarkReceiveTarget? fallback = null,
        string nyxApiKey = "nyx-api-key",
        string nyxProviderSlug = "api-lark-bot",
        string messageType = "text",
        string contentJson = """{"text":"hello"}""",
        LarkReceiveTarget? primaryTarget = null) =>
        new(
            nyxApiKey,
            nyxProviderSlug,
            messageType,
            contentJson,
            primaryTarget ?? new LarkReceiveTarget("oc_primary", "chat_id", FellBackToPrefixInference: false),
            fallback);

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        public SequencedHandler(params string[] responses)
            : this(responses.Select(static response => (HttpStatusCode.OK, response)).ToArray())
        {
        }

        public SequencedHandler(params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, OkResponse);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
