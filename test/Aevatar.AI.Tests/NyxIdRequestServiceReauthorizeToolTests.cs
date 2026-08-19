using System.Net;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdRequestServiceReauthorizeToolTests
{
    private const string ActiveServiceAlpha = """
        {
          "id": "service-alpha",
          "api_key_id": "credential-alpha",
          "status": "active",
          "is_active": true,
          "connected": true,
          "connection_status": "active",
          "granted_scopes": ["read:user"],
          "last_authorized_at": "2026-08-10T07:00:00Z"
        }
        """;

    [Fact]
    public void Tool_ShouldExposeTypedHumanSessionOnlyReadOnlySurface()
    {
        var tool = CreateTool(new StubHandler(ActiveServiceAlpha));

        tool.Name.Should().Be("nyxid_request_service_reauthorize");
        tool.IsReadOnly.Should().BeTrue();
        ((IAgentToolCapabilityDescriptor)tool).Capabilities.Should()
            .BeEquivalentTo(NyxIdToolSurfaces.HumanSessionOnly);
        tool.ParametersSchema.Should().Contain("\"user_service_id\"")
            .And.Contain("\"requested_scopes\"")
            .And.Contain("\"additionalProperties\": false");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEmitTypedRequirementForExactOwnerService()
    {
        var handler = new StubHandler(ActiveServiceAlpha);
        var tool = CreateTool(handler);
        const string arguments =
            """{"user_service_id":"service-alpha","requested_scopes":["repo","read:org"]}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-reauthorize", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/keys/service-alpha");
            handler.Methods.Should().OnlyContain(static method => method == HttpMethod.Get);
            handler.BearerTokens.Should().Equal("runtime-caller-credential");
            result.Should().Contain("\"blocked\":true")
                .And.Contain("\"action\":\"service.reauthorize\"");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.AuthorizationRequired);
            receipt.ErrorCode.Should().Be("NYXID_SERVICE_REAUTHORIZATION_REQUIRED");
            receipt.AuthorizationRequired.ServiceSlug.Should().BeEmpty();
            receipt.AuthorizationRequired.KeyCreate.Should().BeNull();
            receipt.AuthorizationRequired.KeyRotate.Should().BeNull();
            receipt.AuthorizationRequired.ServiceReauthorize.UserServiceId.Should().Be("service-alpha");
            receipt.AuthorizationRequired.ServiceReauthorize.RequestedScopes.Should()
                .Equal("repo", "read:org");
            receipt.ToString().Should().NotContain("credential-alpha")
                .And.NotContain("runtime-caller-credential")
                .And.NotContain("token")
                .And.NotContain("secret");
            result.Should().NotContain("credential-alpha")
                .And.NotContain("runtime-caller-credential");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"user_service_id":"service-alpha"}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":[]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":["repo","repo"]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":[" repo"]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":["re po"]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":[""]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":[1]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":"repo"}""")]
    [InlineData("""{"user_service_id":" service-alpha","requested_scopes":["repo"]}""")]
    [InlineData("""{"user_service_id":"service/alpha","requested_scopes":["repo"]}""")]
    [InlineData("""{"user_service_id":"Bearer secret","requested_scopes":["repo"]}""")]
    [InlineData("""{"user_service_id":"service-alpha","requested_scopes":["repo"],"slug":"github"}""")]
    [InlineData("not-json")]
    public async Task ExecuteAsync_ShouldRejectInvalidArgumentsBeforeRead(string arguments)
    {
        var handler = new StubHandler(ActiveServiceAlpha);
        var tool = CreateTool(handler);
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-reauthorize", tool.Name, arguments, result);

            handler.Requests.Should().BeEmpty();
            result.Should().Contain("NYXID_SERVICE_REAUTHORIZE_ARGUMENTS_INVALID");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRequireVerifiedOwnerAuthorityBeforeRead()
    {
        var handler = new StubHandler(ActiveServiceAlpha);
        var tool = CreateTool(handler);
        const string arguments =
            """{"user_service_id":"service-alpha","requested_scopes":["repo"]}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty;
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-reauthorize", tool.Name, arguments, result);

            handler.Requests.Should().BeEmpty();
            result.Should().Contain("NYXID_SERVICE_REAUTHORIZE_CONTEXT_UNAVAILABLE");
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Theory]
    [InlineData("""
        {
          "id": "service-other",
          "status": "active",
          "is_active": true,
          "connection_status": "active",
          "granted_scopes": ["repo"],
          "last_authorized_at": "2026-08-10T07:00:00Z"
        }
        """)]
    [InlineData("""
        {
          "id": "service-alpha",
          "status": "active",
          "is_active": false,
          "connection_status": "active",
          "granted_scopes": ["repo"],
          "last_authorized_at": "2026-08-10T07:00:00Z"
        }
        """)]
    [InlineData("""{"error":"not_found"}""")]
    public async Task ExecuteAsync_ShouldFailClosedWhenExactOwnerServiceIsUnavailable(
        string responseJson)
    {
        var handler = new StubHandler(responseJson);
        var tool = CreateTool(handler);
        const string arguments =
            """{"user_service_id":"service-alpha","requested_scopes":["repo"]}""";
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = CapabilityContext();
        try
        {
            var result = await tool.ExecuteAsync(arguments);
            var receipt = tool.CreateResultReceipt("call-reauthorize", tool.Name, arguments, result);

            handler.Requests.Should().Equal("/api/v1/keys/service-alpha");
            result.Should().Contain("NYXID_SERVICE_REAUTHORIZE_SERVICE_UNAVAILABLE");
            receipt.Should().NotBeNull();
            receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
            receipt.AuthorizationRequired.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void CreateResultReceipt_ShouldRejectResultThatDoesNotEchoExactArguments()
    {
        var tool = CreateTool(new StubHandler(ActiveServiceAlpha));
        const string arguments =
            """{"user_service_id":"service-alpha","requested_scopes":["repo"]}""";
        const string driftedResult =
            """
            {"blocked":true,"action":"service.reauthorize","user_service_id":"service-alpha","requested_scopes":["repo","admin:org"],"reason_code":"NYXID_SERVICE_REAUTHORIZATION_REQUIRED","safe_message":"Re-authorize the exact connected NyxID service in the secure browser action."}
            """;

        var receipt = tool.CreateResultReceipt("call-reauthorize", tool.Name, arguments, driftedResult);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_SERVICE_REAUTHORIZE_RESULT_INVALID");
        receipt.AuthorizationRequired.Should().BeNull();
    }

    private static NyxIdRequestServiceReauthorizeTool CreateTool(StubHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        return new NyxIdRequestServiceReauthorizeTool(client);
    }

    private static AgentToolExecutionContext CapabilityContext() =>
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "caller-alpha",
                null,
                "scope-alpha"),
            Credentials = new AgentToolCredentials(
                "runtime-caller-credential",
                "runtime-organization-credential",
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "nyx-user-alpha"),
        };

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> BearerTokens { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
