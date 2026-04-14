using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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

    // ─── update_token ───

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Requires_RegistrationId()
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
            var result = await tool.ExecuteAsync("""{"action":"update_token"}""");
            result.Should().Contain("registration_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Returns_Error_When_Registration_Not_Found()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
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
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"nonexistent"}""");
            result.Should().Contain("error");
            result.Should().Contain("not found");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Confirms_Via_Version_And_Token()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        // Pre-dispatch: registration exists with old token at version 5
        queryPort.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                    { Id = "bot-1", Platform = "lark", NyxUserToken = "old-token" }),
                // Post-dispatch polls: return updated entry
                Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                    { Id = "bot-1", Platform = "lark", NyxUserToken = "new-token" }));

        queryPort.GetStateVersionAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5),   // before dispatch
                Task.FromResult<long?>(6));   // after dispatch (advanced)

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "new-token" };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"bot-1"}""");
            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("token_updated");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Fails_When_Version_Does_Not_Advance()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        // Registration appears in projection (could be orphaned)
        queryPort.GetAsync("orphan-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "orphan-1", Platform = "lark", NyxUserToken = "stale-token" }));

        // Version never advances — actor dropped the command
        queryPort.GetStateVersionAsync("orphan-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(5));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "stale-token" };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"orphan-1"}""");
            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("error");
            result.Should().Contain("not confirmed");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Fails_When_Version_Advances_But_Token_Wrong()
    {
        // Isolates the token check: version advances (actor persisted something)
        // but the projected token doesn't match the desired value.
        // Without the token check, this would falsely report success.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("bot-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "bot-2", Platform = "lark", NyxUserToken = "wrong-token" }));

        queryPort.GetStateVersionAsync("bot-2", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5),   // before dispatch
                Task.FromResult<long?>(6));   // after dispatch — version advanced

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "desired-token" };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"bot-2"}""");
            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("error");
            result.Should().Contain("not confirmed");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Dispatches_Command_To_Actor()
    {
        // Verifies the tool actually sends the command envelope to the actor.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("bot-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "bot-3", Platform = "lark", NyxUserToken = "old" }));

        // Version advances and token matches — success path
        queryPort.GetStateVersionAsync("bot-3", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(1),
                Task.FromResult<long?>(2));
        // Return updated token on poll
        queryPort.GetAsync("bot-3", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                    { Id = "bot-3", Platform = "lark", NyxUserToken = "old" }),
                Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                    { Id = "bot-3", Platform = "lark", NyxUserToken = "fresh" }));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "fresh" };
        try
        {
            await tool.ExecuteAsync("""{"action":"update_token","registration_id":"bot-3"}""");

            // Actor must have received exactly one HandleEventAsync call
            // with a ChannelBotUpdateTokenCommand carrying the correct payload.
            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Route != null &&
                e.Route.Direct != null &&
                e.Route.Direct.TargetActorId == "channel-bot-registration-store" &&
                e.Payload != null &&
                e.Payload.Is(ChannelBotUpdateTokenCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().RegistrationId == "bot-3" &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().NyxUserToken == "fresh"));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    // ─── encrypt_key parameter ───

    [Fact]
    public void ParametersSchema_Contains_EncryptKey_Property()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var doc = JsonDocument.Parse(tool.ParametersSchema);
        var properties = doc.RootElement.GetProperty("properties");
        properties.TryGetProperty("encrypt_key", out _).Should().BeTrue(
            "the register action should accept an encrypt_key parameter");
    }

    [Fact]
    public async Task ExecuteAsync_Register_Dispatches_EncryptKey_In_Command()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        // Return confirmation on first poll
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "confirmed-id", Platform = "lark" }));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        // No projection port — GetService returns null, which is fine
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            await tool.ExecuteAsync("""
                {
                    "action": "register",
                    "platform": "lark",
                    "nyx_provider_slug": "api-lark-bot",
                    "encrypt_key": "my-secret-encrypt-key"
                }
                """);

            // Verify the actor received a command with encrypt_key set
            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(ChannelBotRegisterCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().EncryptKey == "my-secret-encrypt-key"));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Register_DefaultsEncryptKeyToEmpty_WhenNotProvided()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "confirmed", Platform = "lark" }));

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-bot-registration-store");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("channel-bot-registration-store")
            .Returns(Task.FromResult<IActor?>(actor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        var sp = services.BuildServiceProvider();

        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            { [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token" };
        try
        {
            await tool.ExecuteAsync("""
                {
                    "action": "register",
                    "platform": "lark",
                    "nyx_provider_slug": "api-lark-bot"
                }
                """);

            // Verify encrypt_key defaults to empty
            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(ChannelBotRegisterCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().EncryptKey == ""));
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
