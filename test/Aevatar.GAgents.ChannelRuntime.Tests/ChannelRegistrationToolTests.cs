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
        tool.Description.Should().Contain("register_lark_via_nyx");
    }

    [Fact]
    public void ParametersSchema_Is_Valid_Json()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var act = () => JsonDocument.Parse(tool.ParametersSchema);
        act.Should().NotThrow();
    }

    [Fact]
    public void ParametersSchema_Contains_Lark_Nyx_Provisioning_Fields()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var doc = JsonDocument.Parse(tool.ParametersSchema);
        var properties = doc.RootElement.GetProperty("properties");
        properties.TryGetProperty("app_id", out _).Should().BeTrue();
        properties.TryGetProperty("app_secret", out _).Should().BeTrue();
        properties.TryGetProperty("label", out _).Should().BeTrue();
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
    public async Task ExecuteAsync_RegisterLarkViaNyx_Does_Not_Require_Channel_Runtime_Dependencies()
    {
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.ProvisionAsync(Arg.Any<NyxLarkProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: "reg-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                RelayCallbackUrl: "https://aevatar.example.com/api/webhooks/nyxid-relay",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        var services = new ServiceCollection();
        services.AddSingleton(provisioningService);
        var sp = services.BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                    "action": "register_lark_via_nyx",
                    "app_id": "cli_xxx",
                    "app_secret": "secret",
                    "webhook_base_url": "https://aevatar.example.com"
                }
                """);

            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-1");
            doc.RootElement.GetProperty("nyx_channel_bot_id").GetString().Should().Be("bot-1");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RegisterLarkViaNyx_Passes_Minimal_Lark_Config_To_Service()
    {
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.ProvisionAsync(Arg.Any<NyxLarkProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkProvisioningResult(
                Succeeded: false,
                Status: "error",
                Error: "missing_app_secret")));

        var services = new ServiceCollection();
        services.AddSingleton(provisioningService);
        var sp = services.BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };
        try
        {
            await tool.ExecuteAsync("""
                {
                    "action": "register_lark_via_nyx",
                    "app_id": "cli_xxx",
                    "app_secret": "secret",
                    "label": "Ops Bot",
                    "scope_id": "scope-1",
                    "webhook_base_url": "https://aevatar.example.com",
                    "nyx_provider_slug": "api-lark-bot"
                }
                """);

            await provisioningService.Received(1).ProvisionAsync(
                Arg.Is<NyxLarkProvisioningRequest>(request =>
                    request.AccessToken == "test-token" &&
                    request.AppId == "cli_xxx" &&
                    request.AppSecret == "secret" &&
                    request.Label == "Ops Bot" &&
                    request.ScopeId == "scope-1" &&
                    request.WebhookBaseUrl == "https://aevatar.example.com" &&
                    request.NyxProviderSlug == "api-lark-bot"),
                Arg.Any<CancellationToken>());
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
    public async Task ExecuteAsync_Register_Lark_Direct_Path_Is_Retired()
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
            var result = await tool.ExecuteAsync("""
                {
                    "action":"register",
                    "platform":"lark",
                    "nyx_provider_slug":"api-lark-bot"
                }
                """);

            result.Should().Contain("Direct Lark registration is retired");
            result.Should().Contain("register_lark_via_nyx");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Lark_Relay_Path_Is_Rejected()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("lark-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "lark-1",
                Platform = "lark",
                NyxAgentApiKeyId = "agent-key-1",
            }));

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
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"lark-1"}""");

            result.Should().Contain("not supported on the Nyx relay path");
            await actorRuntime.DidNotReceive().GetAsync(Arg.Any<string>());
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
    public async Task ExecuteAsync_UpdateToken_Accepts_Dispatch_Without_Projection_Confirmation()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "bot-1", Platform = "telegram", NyxUserToken = "old-token" }));

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
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            result.Should().Contain("Read model visibility is asynchronous");
            _ = queryPort.DidNotReceive().GetStateVersionAsync("bot-1", Arg.Any<CancellationToken>());
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
                { Id = "bot-3", Platform = "telegram", NyxUserToken = "old", NyxRefreshToken = "refresh-old" }));

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
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "fresh",
                [LLMRequestMetadataKeys.NyxIdRefreshToken] = "refresh-fresh",
            };
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
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().NyxUserToken == "fresh" &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().NyxRefreshToken == "refresh-fresh"));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_Preserves_Stored_RefreshToken_When_Not_Provided()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("bot-4", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "bot-4", Platform = "telegram", NyxUserToken = "old", NyxRefreshToken = "refresh-old" }));

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
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "fresh",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"update_token","registration_id":"bot-4"}""");
            var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("auto_refresh_ready").GetBoolean().Should().BeTrue();

            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(ChannelBotUpdateTokenCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().RegistrationId == "bot-4" &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().NyxUserToken == "fresh" &&
                e.Payload.Unpack<ChannelBotUpdateTokenCommand>().NyxRefreshToken == "refresh-old"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    // ─── credential_ref parameter ───

    [Fact]
    public void ParametersSchema_Contains_CredentialRef_Property()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var doc = JsonDocument.Parse(tool.ParametersSchema);
        var properties = doc.RootElement.GetProperty("properties");
        properties.TryGetProperty("credential_ref", out _).Should().BeTrue(
            "the register action should accept a credential_ref parameter");
    }

    [Fact]
    public void ParametersSchema_Contains_NyxRefreshToken_Property()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var tool = new ChannelRegistrationTool(sp);

        var doc = JsonDocument.Parse(tool.ParametersSchema);
        var properties = doc.RootElement.GetProperty("properties");
        properties.TryGetProperty("nyx_refresh_token", out _).Should().BeTrue(
            "register and update_token should accept a nyx_refresh_token parameter");
    }

    [Fact]
    public async Task ExecuteAsync_Register_Dispatches_CredentialRef_In_Command()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        // Return confirmation on first poll
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "confirmed-id", Platform = "telegram" }));

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
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
            [LLMRequestMetadataKeys.NyxIdRefreshToken] = "refresh-token-1",
        };
        try
        {
            await tool.ExecuteAsync("""
                {
                    "action": "register",
                    "platform": "telegram",
                    "nyx_provider_slug": "api-telegram-bot",
                    "credential_ref": "vault://channels/lark/reg-1"
                }
                """);

            // Verify the actor received a command with credential_ref set
            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(ChannelBotRegisterCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().CredentialRef == "vault://channels/lark/reg-1" &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().NyxRefreshToken == "refresh-token-1" &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().EncryptKey == ""));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Register_DefaultsCredentialRefToEmpty_WhenNotProvided()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                { Id = "confirmed", Platform = "telegram" }));

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
                    "platform": "telegram",
                    "nyx_provider_slug": "api-telegram-bot"
                }
                """);

            // Verify credential_ref defaults to empty
            await actor.Received(1).HandleEventAsync(Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(ChannelBotRegisterCommand.Descriptor) &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().CredentialRef == "" &&
                e.Payload.Unpack<ChannelBotRegisterCommand>().NyxRefreshToken == "" &&
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
