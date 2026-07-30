using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using Aevatar.AI.ToolProviders.ChannelAdmin;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationToolTests
{
    [Fact]
    public void Metadata_ReflectsRelayOnlyContract()
    {
        var tool = CreateTool();

        tool.Name.Should().Be("channel_registrations");
        tool.Description.Should().Contain("register_channel_via_nyx");
        tool.Description.Should().Contain("platform=lark");
        tool.Description.Should().Contain("platform=telegram");
        tool.Description.Should().NotContain("register_lark_via_nyx");
        tool.Description.Should().NotContain("rebuild_projection");
        tool.Description.Should().NotContain("repair_lark_mirror");
        tool.ParametersSchema.Should().NotContain("rebuild_projection");
        tool.ParametersSchema.Should().NotContain("reason");
        tool.ParametersSchema.Should().Contain("\"platform\"");
        tool.ParametersSchema.Should().Contain("\"credentials\"");
        tool.ParametersSchema.Should().Contain("\"lark\"");
        tool.ParametersSchema.Should().Contain("\"telegram\"");
        JsonDocument.Parse(tool.ParametersSchema).RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .Equal("list", "register_channel_via_nyx", "delete");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenNoNyxTokenIsAvailable()
    {
        AgentToolRequestContext.Current = null;
        try
        {
            var tool = CreateTool();

            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            result.Should().Contain("No NyxID access token available");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_List_ReturnsRelayRegistrations()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                    NyxProviderSlug = "api-lark-bot",
                    ScopeId = "scope-1",
                    WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1",
                    NyxChannelBotId = "bot-1",
                    NyxAgentApiKeyId = "key-1",
                    NyxConversationRouteId = "route-1",
                },
            ]));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"list"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        var registration = doc.RootElement.GetProperty("registrations")[0];
        registration.GetProperty("registration_mode").GetString().Should().Be("nyx_relay_webhook");
        registration.GetProperty("callback_url").GetString().Should().BeEmpty();
        registration.GetProperty("nyx_channel_bot_id").GetString().Should().Be("bot-1");
    }

    [Fact]
    public async Task ExecuteAsync_RegisterChannelViaNyx_LarkReturnsProvisioningResult()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "lark",
                RegistrationId: "reg-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                RelayCallbackUrl: "https://aevatar.example.com/api/webhooks/nyxid-relay",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(new ChannelRelayRegistrationFacade([provisioningService]))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"register_channel_via_nyx","platform":"lark","lark":{"app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","encrypt_key":" encrypt-alpha "},"webhook_base_url":"https://aevatar.example.com","default_skill_name":"whatsapp-reply-draft"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("platform").GetString().Should().Be("lark");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-1");
        await provisioningService.Received(1).ProvisionAsync(
            Arg.Is<NyxChannelBotProvisioningRequest>(request =>
                request.Platform == "lark" &&
                request.AccessToken == "test-token" &&
                request.ScopeId == "scope-1" &&
                request.Lark != null &&
                request.Lark.AppId == "cli_123" &&
                request.Lark.AppSecret == "secret" &&
                request.Lark.VerificationToken == "verify-123" &&
                request.Lark.EncryptKey == "encrypt-alpha" &&
                request.WebhookBaseUrl == "https://aevatar.example.com" &&
                request.DefaultSkillName == "whatsapp-reply-draft"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterChannelViaNyx_TelegramReturnsProvisioningResult()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("telegram");
        provisioningService.ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotProvisioningResult(
                Succeeded: true,
                Status: "accepted",
                Platform: "telegram",
                RegistrationId: "reg-tg-1",
                NyxChannelBotId: "bot-tg-1",
                NyxAgentApiKeyId: "key-tg-1",
                NyxConversationRouteId: "route-tg-1",
                RelayCallbackUrl: "https://aevatar.example.com/api/webhooks/nyxid-relay",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/telegram/bot-tg-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(new ChannelRelayRegistrationFacade([provisioningService]))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"register_channel_via_nyx","platform":"telegram","telegram":{"bot_token":"123:abc"},"webhook_base_url":"https://aevatar.example.com"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("platform").GetString().Should().Be("telegram");
        doc.RootElement.GetProperty("nyx_provider_slug").GetString().Should().Be("api-telegram-bot");
        await provisioningService.Received(1).ProvisionAsync(
            Arg.Is<NyxChannelBotProvisioningRequest>(request =>
                request.Platform == "telegram" &&
                request.AccessToken == "test-token" &&
                request.ScopeId == "scope-1" &&
                request.Credentials != null &&
                request.Credentials["bot_token"] == "123:abc" &&
                request.WebhookBaseUrl == "https://aevatar.example.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterChannelViaNyx_RejectsMissingScopeContext()
    {
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(new ChannelRelayRegistrationFacade([provisioningService]))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken(null);
        var json = await tool.ExecuteAsync(
            """{"action":"register_channel_via_nyx","platform":"lark","lark":{"app_id":"cli_123","app_secret":"secret"},"webhook_base_url":"https://aevatar.example.com"}""");

        json.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().ProvisionAsync(Arg.Any<NyxChannelBotProvisioningRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RebuildProjection_ReturnsUnsupportedAction()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var result = await tool.ExecuteAsync("""{"action":"rebuild_projection"}""");

        result.Should().Contain("Unsupported channel registration action");
        result.Should().Contain("rebuild_projection");
        result.Should().NotContain("retired_action");
        await queryPort.DidNotReceive().QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_ReturnsRetiredError()
    {
        var tool = CreateTool();

        using var scope = PushNyxToken();
        var result = await tool.ExecuteAsync("""{"action":"update_token"}""");
        using var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("error_code").GetString().Should().Be("retired_action");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("update_token is retired");
    }

    [Fact]
    public async Task ExecuteAsync_RegisterLarkViaNyx_ReturnsRetiredError()
    {
        var tool = CreateTool();

        using var scope = PushNyxToken();
        var result = await tool.ExecuteAsync("""{"action":"register_lark_via_nyx"}""");
        using var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("error_code").GetString().Should().Be("retired_action");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("register_channel_via_nyx");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("platform=lark");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_WithoutConfirm_ReturnsConfirmationPayload()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                NyxProviderSlug = "api-lark-bot",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-1",
                NyxConversationRouteId = "route-1",
            }));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("confirm_required");
        doc.RootElement.GetProperty("registration_mode").GetString().Should().Be("nyx_relay_webhook");
        await ((IActorDispatchPort)actorRuntime).DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Delete_WithConfirm_DispatchesUnregisterCommand()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
        };
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(ActorDispatchPortTestSupport.AcceptAsync);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1","confirm":true}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-1");
        doc.RootElement.GetProperty("note").GetString().Should().Contain("Unregister accepted");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotUnregisterCommand>().RegistrationId.Should().Be("reg-1");
        await queryPort.Received(1).GetAsync("reg-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolSource_ReturnsTool_WithTypedDependencies()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(Array.Empty<ChannelBotRegistrationEntry>()));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var provisioningService = Substitute.For<INyxChannelBotProvisioningService>();
        provisioningService.Platform.Returns("lark");
        var registrationFacade = new ChannelRelayRegistrationFacade([provisioningService]);

        var source = new ChannelRegistrationToolSource(queryPort, commandFacade, registrationFacade);
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("channel_registrations");

        using var scope = PushNyxToken();
        var result = await tools[0].ExecuteAsync("""{"action":"list"}""");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);

        await queryPort.Received(1).QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var registrationFacade = new ChannelRelayRegistrationFacade([]);

        var missingQuery = () => new ChannelRegistrationTool(null!, commandFacade, registrationFacade);
        var missingCommand = () => new ChannelRegistrationTool(queryPort, null!, registrationFacade);
        var missingRegistrationFacade = () => new ChannelRegistrationTool(queryPort, commandFacade, null!);
        var missingSourceQuery = () => new ChannelRegistrationToolSource(null!, commandFacade, registrationFacade);
        var missingSourceCommand = () => new ChannelRegistrationToolSource(queryPort, null!, registrationFacade);
        var missingSourceRegistrationFacade = () => new ChannelRegistrationToolSource(queryPort, commandFacade, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingRegistrationFacade.Should().Throw<ArgumentNullException>().WithParameterName("registrationFacade");
        missingSourceQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingSourceCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingSourceRegistrationFacade.Should().Throw<ArgumentNullException>().WithParameterName("registrationFacade");
    }

    [Fact]
    public void DeleteSource_ShouldNotPollReadModelAfterDispatchUnregister()
    {
        var source = File.ReadAllText(GetChannelRegistrationToolSourcePath());
        var dispatchIndex = source.IndexOf("UnregisterAsync", StringComparison.Ordinal);
        dispatchIndex.Should().BeGreaterThanOrEqualTo(0);
        var afterDispatch = source[dispatchIndex..];

        afterDispatch.Should().NotContain("for (var attempt = 0; attempt < 10; attempt++)");
        afterDispatch.Should().NotContain("for (var i = 0; i < 10; i++)");
        afterDispatch.Should().NotContain(string.Concat("Task", ".Delay(500"));
        afterDispatch.Should().NotContain("status = confirmed ? \"deleted\" : \"accepted\"");
    }

    private static IDisposable PushNyxToken(string? scopeId = "scope-1")
    {
        var previous = AgentToolRequestContext.Current;
        var next = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
            next["scope_id"] = scopeId;

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(next);

        return new ResetMetadataScope(previous);
    }

    private static ChannelRegistrationTool CreateTool(IServiceProvider? services = null)
    {
        var provider = services ?? CreateDefaultServices().BuildServiceProvider();
        return new ChannelRegistrationTool(
            provider.GetService<IChannelBotRegistrationQueryPort>() ?? Substitute.For<IChannelBotRegistrationQueryPort>(),
            provider.GetService<ChannelRegistrationCommandFacade>() ?? CreateDefaultCommandFacade(),
            provider.GetService<ChannelRelayRegistrationFacade>() ?? new ChannelRelayRegistrationFacade([]));
    }

    private static IServiceCollection CreateDefaultServices()
    {
        return new ServiceCollection()
            .AddSingleton(Substitute.For<IChannelBotRegistrationQueryPort>())
            .AddSingleton(CreateDefaultCommandFacade())
            .AddSingleton(new ChannelRelayRegistrationFacade([]));
    }

    private static ChannelRegistrationCommandFacade CreateDefaultCommandFacade()
    {
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        return ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
    }

    private sealed class ResetMetadataScope(AgentToolExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.Current = previous;
    }

    private static string GetChannelRegistrationToolSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Aevatar.AI.ToolProviders.ChannelAdmin",
                "ChannelRegistrationTool.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ChannelRegistrationTool.cs from test output directory.");
    }
}
