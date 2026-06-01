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
        tool.Description.Should().Contain("register_lark_via_nyx");
        tool.Description.Should().NotContain("rebuild_projection");
        tool.Description.Should().NotContain("repair_lark_mirror");
        tool.ParametersSchema.Should().NotContain("rebuild_projection");
        tool.ParametersSchema.Should().NotContain("reason");
        JsonDocument.Parse(tool.ParametersSchema).RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .Equal("list", "register_lark_via_nyx", "delete");
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
    public async Task ExecuteAsync_RegisterLarkViaNyx_ReturnsProvisioningResult()
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

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"register_lark_via_nyx","app_id":"cli_123","app_secret":"secret","verification_token":"verify-123","webhook_base_url":"https://aevatar.example.com"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-1");
        await provisioningService.Received(1).ProvisionAsync(
            Arg.Is<NyxLarkProvisioningRequest>(request =>
                request.AccessToken == "test-token" &&
                request.ScopeId == "scope-1" &&
                request.AppId == "cli_123" &&
                request.AppSecret == "secret" &&
                request.VerificationToken == "verify-123" &&
                request.WebhookBaseUrl == "https://aevatar.example.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterLarkViaNyx_RejectsMissingScopeContext()
    {
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken(null);
        var json = await tool.ExecuteAsync(
            """{"action":"register_lark_via_nyx","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""");

        json.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().ProvisionAsync(Arg.Any<NyxLarkProvisioningRequest>(), Arg.Any<CancellationToken>());
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
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.Platform.Returns("lark");

        var source = new ChannelRegistrationToolSource(queryPort, commandFacade, provisioningService);
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
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();

        var missingQuery = () => new ChannelRegistrationTool(null!, commandFacade, provisioningService);
        var missingCommand = () => new ChannelRegistrationTool(queryPort, null!, provisioningService);
        var missingProvisioning = () => new ChannelRegistrationTool(queryPort, commandFacade, null!);
        var missingSourceQuery = () => new ChannelRegistrationToolSource(null!, commandFacade, provisioningService);
        var missingSourceCommand = () => new ChannelRegistrationToolSource(queryPort, null!, provisioningService);
        var missingSourceProvisioning = () => new ChannelRegistrationToolSource(queryPort, commandFacade, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingProvisioning.Should().Throw<ArgumentNullException>().WithParameterName("provisioningService");
        missingSourceQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingSourceCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingSourceProvisioning.Should().Throw<ArgumentNullException>().WithParameterName("provisioningService");
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
            provider.GetService<INyxLarkProvisioningService>() ?? Substitute.For<INyxLarkProvisioningService>());
    }

    private static IServiceCollection CreateDefaultServices()
    {
        return new ServiceCollection()
            .AddSingleton(Substitute.For<IChannelBotRegistrationQueryPort>())
            .AddSingleton(CreateDefaultCommandFacade())
            .AddSingleton(Substitute.For<INyxLarkProvisioningService>());
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
