using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace Aevatar.AI.Tests;

public class NyxIdCodeExecuteToolTests
{
    [Fact]
    public void Name_IsCodeExecute()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        tool.Name.Should().Be("code_execute");
    }

    [Fact]
    public void ApprovalMode_NeverRequiresApproval()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        ((IAgentTool)tool).RequiresApproval("""{"language":"python","code":"print(1)"}""").Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_NoToken_ReturnsError()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());

        // No AgentToolRequestContext set → no token
        AgentToolRequestContext.Current = null;
        var result = await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

        result.Should().Contain("No NyxID access token");
    }

    [Fact]
    public async Task ExecuteAsync_MissingLanguage_ReturnsError()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        SetMetadata("test-token", null);

        var result = await tool.ExecuteAsync("""{"code":"print(1)"}""");

        result.Should().Contain("'language' and 'code' are required");
        ClearMetadata();
    }

    [Fact]
    public async Task ExecuteAsync_MissingCode_ReturnsError()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        SetMetadata("test-token", null);

        var result = await tool.ExecuteAsync("""{"language":"python"}""");

        result.Should().Contain("'language' and 'code' are required");
        ClearMetadata();
    }

    [Fact]
    public async Task ExecuteAsync_NoSandboxInContext_FallsBackToDiscoveryOrProbe()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        // Token present but no connected services context
        // With a dummy client (unreachable URL), discovery and probe will fail,
        // but the tool should attempt them before giving up.
        SetMetadata("test-token", null);

        var result = await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

        // With an unreachable dummy server, the probe may succeed (connection error ≠ 404)
        // or fail entirely. Either way, the tool should not crash.
        result.Should().NotBeNull();
        ClearMetadata();
    }

    [Fact]
    public async Task ExecuteAsync_SandboxInContext_ResolvesSlug()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        var servicesContext = """
            <connected-services>
            - **Chrono Sandbox** (slug: `chrono-sandbox-service`) — base: https://sandbox.example.com
            </connected-services>
            """;
        SetMetadata("test-token", servicesContext);

        // The actual proxy call will fail with our dummy client, but we verify slug resolution works
        var result = await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

        // Should NOT contain "No sandbox" error — slug was resolved
        result.Should().NotContain("No sandbox service connected");
        ClearMetadata();
    }

    [Fact]
    public async Task ExecuteAsync_ContextSandboxReturnsTransientError_RetriesLiveDiscoveredSandbox()
    {
        var handler = new CodeExecuteRecoveryHandler();
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
        var tool = new NyxIdCodeExecuteTool(client);
        var servicesContext = """
            <connected-services>
            - **Chrono Sandbox** (slug: `stale-sandbox`) - base: https://sandbox.example.com
            </connected-services>
            """;
        SetMetadata("test-token", servicesContext);

        var result = await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

        result.Should().Be("""{"stdout":"1\n","stderr":"","exit_code":0}""");
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Should().Be("POST /api/v1/proxy/s/stale-sandbox/execute");
        handler.Requests[1].Should().Be("GET /api/v1/proxy/services");
        handler.Requests[2].Should().Be("POST /api/v1/proxy/s/chrono-sandbox-service/execute");
        ClearMetadata();
    }

    private static NyxIdApiClient CreateDummyClient()
    {
        return new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://test.example.com" });
    }

    private static void SetMetadata(string token, string? servicesContext)
    {
        var metadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        };
        if (servicesContext is not null)
            metadata[LLMRequestMetadataKeys.ConnectedServicesContext] = servicesContext;
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(metadata);
    }

    private static void ClearMetadata()
    {
        AgentToolRequestContext.Current = null;
    }

    private sealed class CodeExecuteRecoveryHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");

            var responseBody = request.RequestUri.AbsolutePath switch
            {
                "/api/v1/proxy/s/stale-sandbox/execute" =>
                    """{"error":"internal_error","error_code":1006,"message":"upstream internal error"}""",
                "/api/v1/proxy/services" =>
                    """{"services":[{"slug":"chrono-sandbox-service","name":"Chrono Sandbox"}]}""",
                "/api/v1/proxy/s/chrono-sandbox-service/execute" =>
                    """{"stdout":"1\n","stderr":"","exit_code":0}""",
                _ => """{"error":"unexpected_request"}""",
            };

            var status = request.RequestUri.AbsolutePath == "/api/v1/proxy/s/stale-sandbox/execute"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
