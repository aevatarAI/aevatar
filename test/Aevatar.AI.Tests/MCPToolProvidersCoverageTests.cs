using System.Text.Json;
using Aevatar.AI.ToolProviders.MCP;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class MCPToolProvidersCoverageTests
{
    [Fact]
    public void SanitizeToolName_ShouldNormalizeAndFallback()
    {
        new MCPToolAdapter("Weather Tool!*", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("Weather_Tool");
        new MCPToolAdapter("a!!!b", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("a_b");
        new MCPToolAdapter("___", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("unnamed_tool");
        new MCPToolAdapter("name__", "desc", "{}", client: null!, serverName: "srv")
            .Name.Should().Be("name");
    }

    [Fact]
    public async Task ExecuteAsync_WhenArgumentsJsonInvalid_ShouldReturnErrorPayload()
    {
        var adapter = new MCPToolAdapter("tool", "desc", "{}", client: null!, serverName: "srv");

        var result = await adapter.ExecuteAsync("{invalid");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientUnavailable_ShouldReturnErrorPayload()
    {
        var adapter = new MCPToolAdapter("tool", "desc", "{}", client: null!, serverName: "srv");

        var result = await adapter.ExecuteAsync("{}");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue();
        error.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DiscoverToolsAsync_WhenServerConnectFails_ShouldReturnCachedEmptyResult()
    {
        var options = new MCPToolsOptions().AddServer("bad", "/path/does/not/exist");
        var source = new MCPAgentToolSource(options, new MCPClientManager());

        var first = await source.DiscoverToolsAsync();
        var second = await source.DiscoverToolsAsync();

        first.Should().BeEmpty();
        ReferenceEquals(first, second).Should().BeTrue();
    }
}
