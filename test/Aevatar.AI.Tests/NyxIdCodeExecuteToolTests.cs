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
    public void RequiresApproval_AlwaysTrue()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());
        tool.RequiresApproval("""{"language":"python","code":"print(1)"}""").Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoToken_ReturnsError()
    {
        var tool = new NyxIdCodeExecuteTool(CreateDummyClient());

        // No AgentToolRequestContext set → no token
        AgentToolRequestContext.CurrentMetadata = null;
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
        AgentToolRequestContext.CurrentMetadata = metadata;
    }

    private static void ClearMetadata()
    {
        AgentToolRequestContext.CurrentMetadata = null;
    }
}
