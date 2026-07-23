using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdProxyToolExactIdentityTests
{
    [Fact]
    public void ParametersSchema_ShouldRequireAdmittedExactOperationTuple()
    {
        var tool = CreateTool(new CountingHandler());

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("service_id", "slug", "path");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExactIdentity_ShouldRejectBeforeDiscovery()
    {
        var handler = new CountingHandler();
        var tool = CreateTool(handler);
        using var _scope = PushContext();

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("typed capability discovery");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithSlugButNoServiceId_ShouldRejectBeforeProxy()
    {
        var handler = new CountingHandler();
        var tool = CreateTool(handler);
        using var _scope = PushContext();

        var result = await tool.ExecuteAsync(
            """{"slug":"home-assistant","path":"/api/items","method":"GET"}""");

        result.Should().Contain("service_id");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateResultReceipt_WithSlugButNoServiceId_ShouldReturnTypedFailure()
    {
        var handler = new CountingHandler();
        var tool = CreateTool(handler);
        const string arguments =
            """{"slug":"home-assistant-q1000","path":"/q1000","method":"GET"}""";
        using var _scope = PushContext();

        var result = await tool.ExecuteAsync(arguments);
        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_PROXY_SERVICE_ID_REQUIRED");
        receipt.ErrorMessage.Should().Be("'service_id' is required when 'slug' is provided");
        receipt.ResultJson.Should().Be(result);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void CreateResultReceipt_WithValidExactIdentity_ShouldNotInferFailureFromDomainJson()
    {
        var tool = CreateTool(new CountingHandler());
        const string arguments =
            """{"service_id":"us-home-alpha","slug":"home-assistant-q1000","path":"/q1000","method":"GET"}""";
        const string domainResult =
            """{"error":"'service_id' is required when 'slug' is provided"}""";

        var receipt = tool.CreateResultReceipt("call-1", tool.Name, arguments, domainResult);

        receipt.Should().BeNull();
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-API-Key")]
    public async Task ExecuteAsync_WithSensitiveHeader_ShouldRejectBeforeProxy(string headerName)
    {
        var handler = new CountingHandler();
        var tool = CreateTool(handler);
        using var _scope = PushContext();

        var result = await tool.ExecuteAsync(
            $$$"""{"service_id":"us-home-alpha","slug":"home-assistant","path":"/api/items","method":"GET","headers":{"{{{headerName}}}":"forbidden-value"}}""");

        result.Should().Contain("sensitive header");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public void CreateResultReceipt_WithAuthorizationFailure_ShouldPreserveExactServiceIdentity()
    {
        var tool = CreateTool(new CountingHandler());
        const string result =
            """{"error":true,"status":401,"body":"{\"error\":\"unauthorized\",\"error_code\":1001}"}""";

        var receipt = tool.CreateResultReceipt(
            "call-1",
            tool.Name,
            """{"service_id":"us-home-alpha","slug":"home-assistant","path":"/api/items"}""",
            result);

        receipt.Should().NotBeNull();
        receipt!.AuthorizationRequired.UserServiceId.Should().Be("us-home-alpha");
    }

    private static NyxIdProxyTool CreateTool(CountingHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        return new NyxIdProxyTool(new NyxIdApiClient(options, new HttpClient(handler)));
    }

    private static AgentToolContextScope PushContext() =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("user-token", null, null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
