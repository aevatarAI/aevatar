using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Tests for ChannelRegistrationTool — verifies lazy DI resolution,
/// tool metadata, and error handling when dependencies are unavailable.
/// </summary>
public class ChannelRegistrationToolTests
{
    // ─── Tool metadata ───

    [Fact]
    public void Name_Is_channel_registrations()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        tool.Name.Should().Be("channel_registrations");
    }

    [Fact]
    public void Description_Contains_Platform_Names()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        tool.Description.Should().Contain("Lark");
        tool.Description.Should().Contain("Telegram");
    }

    [Fact]
    public void ParametersSchema_Is_Valid_Json()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var act = () => JsonDocument.Parse(tool.ParametersSchema);
        act.Should().NotThrow();
    }

    // ─── Lazy DI resolution: error when dependencies missing ───

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_QueryPort_Not_Registered()
    {
        // Arrange: IServiceProvider has no IChannelBotRegistrationQueryPort
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        // Simulate authenticated context
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            // Act
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            // Assert: clear error message, not a crash
            result.Should().Contain("error");
            result.Should().Contain("not registered");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_No_Auth_Token()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        // No AgentToolRequestContext set
        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Contain("error");
        result.Should().Contain("access token");
    }

    // ─── Lazy DI resolution: works when dependencies available ───

    [Fact]
    public async Task ExecuteAsync_List_Returns_Empty_When_No_Registrations()
    {
        // Arrange: mock dependencies
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([]));

        var actorRuntime = Substitute.For<IActorRuntime>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            // Act
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            // Assert
            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Register_Requires_Platform()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([]));
        var actorRuntime = Substitute.For<IActorRuntime>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"register"}""");
            result.Should().Contain("platform");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_RegistrationId()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete"}""");
            result.Should().Contain("registration_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    // ─── ChannelRegistrationToolSource ───

    [Fact]
    public async Task ToolSource_Always_Returns_Tool()
    {
        // The tool source should always return the tool — the tool itself
        // handles missing dependencies gracefully at ExecuteAsync time.
        var sp = new ServiceCollection().BuildServiceProvider();
        var source = new ChannelRegistrationToolSource(sp);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("channel_registrations");
    }
}
