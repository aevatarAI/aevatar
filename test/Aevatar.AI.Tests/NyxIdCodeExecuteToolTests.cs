using System.Net;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

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
    public async Task ExecuteAsync_NoSandboxInContext_UsesDefaultConfiguredRoute()
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            httpClient);
        var tool = new NyxIdCodeExecuteTool(client);
        SetMetadata("test-token", null);

        try
        {
            await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

            handler.LastRequestUri.Should().Be(
                "https://nyx.example/api/v1/proxy/s/chrono-sandbox/execute");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public async Task ExecuteAsync_SandboxInContext_ResolvesSlug()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        var servicesContext = """
            <connected-services>
            - **Chrono Sandbox** (slug: `chrono-sandbox`) — base: https://sandbox.example.com
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
    public async Task ExecuteAsync_WithoutSandboxContext_UsesConfiguredServiceSlug()
    {
        var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            httpClient);
        var tool = new NyxIdCodeExecuteTool(
            client,
            sandboxServiceSlug: "sandbox-custom");
        SetMetadata("test-token", null);

        try
        {
            await tool.ExecuteAsync("""{"language":"python","code":"print(1)"}""");

            handler.LastRequestUri.Should().Be(
                "https://nyx.example/api/v1/proxy/s/sandbox-custom/execute");
        }
        finally
        {
            ClearMetadata();
        }
    }

    [Fact]
    public void CreateResultReceipt_Http401_ShouldReturnTypedFailure()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        const string result = """{"error":true,"status":401,"body":"unauthorized"}""";

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-401",
            tool.Name,
            """{"language":"python","code":"print(1)"}""",
            result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("NYXID_PROXY_UNAUTHORIZED");
        receipt.ResultJson.Should().NotContain("unauthorized");
    }

    [Fact]
    public void CreateResultReceipt_SandboxResult_ShouldReturnTypedSuccess()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        const string result = """{"stdout":"1\n","stderr":"","exit_code":0}""";

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-success",
            tool.Name,
            """{"language":"python","code":"print(1)"}""",
            result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
    }

    [Fact]
    public void CreateResultReceipt_ErrorJsonWithoutException_ShouldReturnTypedFailure()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        const string result = """{"error":"sandbox unavailable: provider-secret"}""";

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-error",
            tool.Name,
            """{"language":"python","code":"print(1)"}""",
            result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("CODE_EXECUTE_FAILED");
        receipt.ResultJson.Should().NotContain("provider-secret");
    }

    [Fact]
    public void CreateResultReceipt_UnrecognizedJson_ShouldLeaveOutcomeUnverified()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());

        var receipt = ((IAgentTool)tool).CreateResultReceipt(
            "call-unknown",
            tool.Name,
            """{"language":"python","code":"print(1)"}""",
            "{}");

        receipt.Should().BeNull();
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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true}"""),
            });
        }
    }
}
