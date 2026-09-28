using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdConnectLinksToolTests
{
    [Fact]
    public async Task ExecuteAsync_Create_ShouldPostConnectLinkRequestAndRedactReceiptUrl()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"link-1","connect_url":"https://nyx.example/connect/secret-token","expires_at":"2026-09-28T10:00:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var client = CreateClient(handler);
        var tool = new NyxIdConnectLinksTool(client);
        const string arguments = """{"action":"create","service_slug":"api-github","label":"GitHub","requested_by":"default-skill","callback_url":"https://callback.example/nyx","expires_in":900,"target_org_id":"org-1"}""";

        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.GetCallSafety(arguments).RequiresApproval.Should().BeTrue();

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-create", tool.Name, arguments, result);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/connect-links");
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer request-token");
        handler.LastRequestBody.Should().Contain("\"service_slug\":\"api-github\"");
        handler.LastRequestBody.Should().Contain("\"label\":\"GitHub\"");
        handler.LastRequestBody.Should().Contain("\"requested_by\":\"default-skill\"");
        handler.LastRequestBody.Should().Contain("\"callback_url\":\"https://callback.example/nyx\"");
        handler.LastRequestBody.Should().Contain("\"expires_in\":900");
        handler.LastRequestBody.Should().Contain("\"target_org_id\":\"org-1\"");
        result.Should().Contain("https://nyx.example/connect/secret-token");
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ResultJson.Should().NotContain("secret-token");
        receipt.ResultJson.Should().Contain("[redacted]");
        ((IAgentToolLiveResultMapper)tool)
            .ResolveLiveResultJson(arguments, result, receipt)
            .Should().Contain("https://nyx.example/connect/secret-token");
    }

    [Fact]
    public async Task ExecuteAsync_ShowAndCancel_ShouldCallExpectedConnectLinkRoutes()
    {
        var showHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"link-1","status":"pending","service_slug":"api-github","expires_at":"2026-09-28T10:00:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var showClient = CreateClient(showHandler);
        var showTool = new NyxIdConnectLinksTool(showClient);

        using var _scope = PushToken();
        await showTool.ExecuteAsync("""{"action":"show","id":"link-1"}""");

        showHandler.LastRequest.Should().NotBeNull();
        showHandler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        showHandler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/connect-links/link-1");

        var cancelHandler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"link-1","status":"cancelled","service_slug":"api-github","expires_at":"2026-09-28T10:00:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var cancelClient = CreateClient(cancelHandler);
        var cancelTool = new NyxIdConnectLinksTool(cancelClient);

        await cancelTool.ExecuteAsync("""{"action":"cancel","id":"link-1"}""");

        cancelHandler.LastRequest.Should().NotBeNull();
        cancelHandler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        cancelHandler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/connect-links/link-1/cancel");
        cancelHandler.LastRequestBody.Should().Be("{}");
    }

    [Fact]
    public async Task ExecuteAsync_MissingToken_ShouldReturnClearError()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var client = CreateClient(handler);
        var tool = new NyxIdConnectLinksTool(client);

        var result = await tool.ExecuteAsync("""{"action":"create","service_slug":"api-github"}""");

        result.Should().Contain("No NyxID access token");
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateResultReceipt_HttpError_ShouldNotExposeProviderBody()
    {
        using var client = CreateClient(new CaptureHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                """{"message":"connect failed for bearer-secret"}""",
                Encoding.UTF8,
                "application/json"),
        }));
        var tool = new NyxIdConnectLinksTool(client);
        const string arguments = """{"action":"create","service_slug":"api-github"}""";

        using var _scope = PushToken();
        var result = await tool.ExecuteAsync(arguments);
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-error", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ResultJson.Should().NotContain("bearer-secret");
        receipt.ErrorMessage.Should().Be("The NyxID request failed.");
    }

    [Fact]
    public async Task ToolSource_ShouldExposeOnlyConnectLinksTool_WhenNyxIdIsConfigured()
    {
        using var client = CreateClient(new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));
        var source = new NyxIdConnectLinksToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            client);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("nyxid_connect_links");
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

    private static AgentToolContextScope PushToken() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("request-token", null, null),
        });

    private sealed class CaptureHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
