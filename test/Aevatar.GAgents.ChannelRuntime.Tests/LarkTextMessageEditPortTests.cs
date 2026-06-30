using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkTextMessageEditPortTests
{
    [Fact]
    public async Task EditAsync_Success_PutsExpectedPathAndBody()
    {
        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{}}""");
        var port = CreatePort(handler);

        var result = await port.EditAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Put);
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot-4/open-apis/im/v1/messages/om_1");
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be("nyx-proxy-bearer-token");

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        var contentJson = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentJson!);
        content.RootElement.GetProperty("text").GetString().Should().Be("updated text");
    }

    public static TheoryData<LarkTextMessageEditRequest, string> InvalidRequests => new()
    {
        { CreateRequest(nyxProxyBearerToken: " "), "NyxID proxy bearer token is required." },
        { CreateRequest(nyxProviderSlug: " "), "NyxID provider slug is required." },
        { CreateRequest(messageId: " "), "Lark message id is required." },
        { CreateRequest(text: " "), "Lark message text is required." },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task EditAsync_InvalidRequest_ThrowsBeforeHttpDispatch(
        LarkTextMessageEditRequest request,
        string expectedMessage)
    {
        var handler = new RecordingHandler("""{"code":0,"msg":"success","data":{}}""");
        var port = CreatePort(handler);

        var act = () => port.EditAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request")
            .WithMessage(expectedMessage + "*");
        handler.Requests.Should().BeEmpty();
        handler.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task EditAsync_DirectLarkError_ReturnsFailedResult()
    {
        var handler = new RecordingHandler("""{"code":230002,"msg":"Bot is not in the chat"}""");
        var port = CreatePort(handler);

        var result = await port.EditAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().Be(230002);
        result.Detail.Should().Be("Bot is not in the chat");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task EditAsync_NestedNyxProxyError_ReturnsNestedLarkCode()
    {
        var nested = JsonSerializer.Serialize(new { code = 99992364, msg = "user id cross tenant" });
        var envelope = JsonSerializer.Serialize(new
        {
            error = true,
            status = 400,
            body = nested,
        });
        var handler = new RecordingHandler(envelope);
        var port = CreatePort(handler);

        var result = await port.EditAsync(CreateRequest(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.LarkCode.Should().Be(LarkBotErrorCodes.UserIdCrossTenant);
        result.Detail.Should().Contain("nyx_status=400");
        result.Detail.Should().Contain("lark_code=99992364");
        result.Detail.Should().Contain("user id cross tenant");
        handler.Requests.Should().ContainSingle();
    }

    private static LarkTextMessageEditPort CreatePort(HttpMessageHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new LarkTextMessageEditPort(client, NullLogger<LarkTextMessageEditPort>.Instance);
    }

    private static LarkTextMessageEditRequest CreateRequest(
        string nyxProxyBearerToken = "nyx-proxy-bearer-token",
        string nyxProviderSlug = "api-lark-bot-4",
        string messageId = "om_1",
        string text = "updated text") =>
        new(nyxProxyBearerToken, nyxProviderSlug, messageId, text);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> Bodies { get; } = [];

        public RecordingHandler(string body)
            : this(HttpStatusCode.OK, body)
        {
        }

        public RecordingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
