using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
        tool.Description.Should().Contain("Bot provisioning is NyxID-owned");
        tool.Description.Should().NotContain("platform=lark");
        tool.Description.Should().NotContain("platform=telegram");
        tool.Description.Should().NotContain("register_lark_via_nyx");
        tool.Description.Should().NotContain("rebuild_projection");
        tool.Description.Should().NotContain("repair_lark_mirror");
        tool.ParametersSchema.Should().NotContain("rebuild_projection");
        tool.ParametersSchema.Should().NotContain("reason");
        tool.ParametersSchema.Should().NotContain("\"platform\"");
        tool.ParametersSchema.Should().NotContain("\"credentials\"");
        tool.ParametersSchema.Should().NotContain("\"lark\"");
        tool.ParametersSchema.Should().NotContain("\"telegram\"");
        tool.ParametersSchema.Should().NotContain("\"bot_token\"");
        tool.ParametersSchema.Should().NotContain("\"service_ids\"");
        tool.ParametersSchema.Should().NotContain("\"authorization_mode\"");
        tool.ParametersSchema.Should().NotContain("scope_plan_digest");
        tool.ParametersSchema.Should().NotContain("allowed_service_ids");
        tool.ParametersSchema.Should().NotContain("agent_key_service_requirements");
        tool.ParametersSchema.Should().NotContain("allowed_service_slugs");
        JsonDocument.Parse(tool.ParametersSchema).RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .Equal("list", "delete");
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
        queryPort.QueryAllSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
            [
                new(new ChannelBotRegistrationEntry
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
                    11),
                new(new ChannelBotRegistrationEntry
                    {
                        Id = "reg-foreign",
                        Platform = "lark",
                        NyxProviderSlug = "api-lark-bot",
                        ScopeId = "scope-other",
                        WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-2",
                        NyxChannelBotId = "bot-2",
                        NyxAgentApiKeyId = "key-2",
                        NyxConversationRouteId = "route-2",
                    },
                    12),
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
        registration.GetProperty("id").GetString().Should().Be("reg-1");
        registration.GetProperty("registration_mode").GetString().Should().Be("nyx_relay_webhook");
        registration.GetProperty("callback_url").GetString().Should().BeEmpty();
        registration.GetProperty("nyx_channel_bot_id").GetString().Should().Be("bot-1");
        json.Should().NotContain("reg-foreign");
        json.Should().NotContain("scope-other");
    }

    [Fact]
    public async Task ExecuteAsync_List_ExposesOnlySafeAuthorizationSummaryAndStateVersion()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
            [
                new(NewModelRegistration("reg-new", "scope-1", "key-new"), 51),
                new(ExplicitModelRegistration(
                    "reg-explicit-list",
                    "scope-1",
                    "key-explicit-list",
                    "svc-alpha",
                    "svc-beta"), 53),
                new(ExplicitModelRegistration(
                    "reg-explicit-empty",
                    "scope-1",
                    "key-explicit-empty"), 52),
                new(new ChannelBotRegistrationEntry
                    {
                        Id = "reg-invalid-new",
                        Platform = "telegram",
                        ScopeId = "scope-1",
                        NyxAgentApiKeyId = "key-invalid-new",
                        AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
                    },
                    50),
                new(new ChannelBotRegistrationEntry
                    {
                        Id = "reg-legacy",
                        Platform = "lark",
                        ScopeId = "scope-1",
                        NyxAgentApiKeyId = "key-legacy",
                    },
                    49),
            ]));
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"list"}""");
        using var document = JsonDocument.Parse(json);

        var registrations = document.RootElement.GetProperty("registrations");
        var newRegistration = registrations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-new");
        newRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("nyxid_default");
        newRegistration.GetProperty("state_version").GetInt64().Should().Be(51);
        newRegistration.TryGetProperty("service_ids", out _).Should().BeFalse();
        var explicitRegistration = registrations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-explicit-list");
        explicitRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("explicit_service_allowlist");
        explicitRegistration.GetProperty("service_ids").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("svc-alpha", "svc-beta");
        var explicitEmptyRegistration = registrations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-explicit-empty");
        explicitEmptyRegistration.GetProperty("authorization_mode").GetString()
            .Should().Be("explicit_service_allowlist");
        explicitEmptyRegistration.GetProperty("service_ids").EnumerateArray().Should().BeEmpty();
        var invalidNew = registrations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-invalid-new");
        invalidNew.GetProperty("authorization_mode").GetString()
            .Should().Be("nyxid_default");
        invalidNew.GetProperty("state_version").GetInt64().Should().Be(50);
        var legacyRegistration = registrations.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "reg-legacy");
        legacyRegistration.TryGetProperty("authorization_mode", out _).Should().BeFalse();
        legacyRegistration.TryGetProperty("service_ids", out _).Should().BeFalse();
        legacyRegistration.GetProperty("state_version").GetInt64().Should().Be(49);
        json.Should().NotContain("sec-reg-new");
        json.Should().NotContain("secret_reference");
        json.Should().NotContain("channel_agent_key");
        json.Should().NotContain("allowed_service_ids");
        json.Should().NotContain("allowed_node_ids");
        json.Should().NotContain("allow_all_services");
        json.Should().NotContain("scope_plan_digest");
        json.Should().NotContain("fingerprint");
        await queryPort.DidNotReceive().GetStateVersionAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_List_RequiresScopeContext()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken(null);
        var json = await tool.ExecuteAsync("""{"action":"list"}""");

        json.Should().Contain("scope_id is required");
        await queryPort.DidNotReceive().QueryAllSnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterChannelViaNyx_ReturnsRetiredError()
    {
        var tool = CreateTool();

        using var scope = PushNyxToken();
        var result = await tool.ExecuteAsync(
            """{"action":"register_channel_via_nyx","platform":"lark","lark":{"app_id":"cli_123","app_secret":"secret"},"webhook_base_url":"https://aevatar.example.com"}""");
        using var document = JsonDocument.Parse(result);

        document.RootElement.GetProperty("error_code").GetString().Should().Be("retired_action");
        document.RootElement.GetProperty("error").GetString().Should().Contain("register_channel_via_nyx is retired");
        document.RootElement.GetProperty("error").GetString().Should().Contain("/api/channels/registrations");
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
        await queryPort.DidNotReceive().QueryAllSnapshotsAsync(Arg.Any<CancellationToken>());
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
        doc.RootElement.GetProperty("error").GetString().Should().Contain("register_lark_via_nyx is retired");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("/api/channels/registrations");
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
                ScopeId = "scope-1",
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
            ScopeId = "scope-1",
            NyxConversationRouteId = "route-1",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
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
        var deprovisioningService = CreateSuccessfulDeprovisioningService();

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime))
            .AddSingleton(deprovisioningService)
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
        await deprovisioningService.Received(1).DeprovisionAsync(
            "test-token",
            Arg.Is<NyxChannelBotDeprovisioningRequest>(request =>
                request.RegistrationId == "reg-1" &&
                request.ConversationRouteId == "route-1" &&
                request.ChannelBotId == "bot-1" &&
                request.AgentKeyId == "key-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Delete_HardAgentKeyFailure_DoesNotDispatchUnregister()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-1",
                Platform = "lark",
                ScopeId = "scope-1",
                NyxChannelBotId = "bot-1",
                NyxAgentApiKeyId = "key-1",
            }));
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var deprovisioningService = Substitute.For<INyxChannelBotDeprovisioningService>();
        deprovisioningService.DeprovisionAsync(
                Arg.Any<string>(),
                Arg.Any<NyxChannelBotDeprovisioningRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(
                ChannelBotRemoved: true,
                AgentKeyRemoved: false,
                Succeeded: false,
                Warnings: Array.Empty<string>())));
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(
                actorRuntime,
                (IActorDispatchPort)actorRuntime))
            .AddSingleton(deprovisioningService)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"delete","registration_id":"reg-1","confirm":true}""");

        json.Should().Contain("nyx_agent_key_delete_failed");
        await ((IActorDispatchPort)actorRuntime).DidNotReceiveWithAnyArgs()
            .DispatchAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ForeignScope_IsIndistinguishableFromMissingRegistration()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-foreign", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-foreign",
                Platform = "lark",
                ScopeId = "scope-other",
                NyxChannelBotId = "bot-2",
            }));
        queryPort.GetAsync("reg-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var foreignJson = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-foreign","confirm":true}""");
        var missingJson = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-missing","confirm":true}""");

        // A caller probing another tenant's registration id must get the exact same
        // payload as for an id that does not exist at all.
        foreignJson.Should().Be(missingJson.Replace("reg-missing", "reg-foreign"));
        foreignJson.Should().Contain("not found");
        foreignJson.Should().NotContain("scope-other");
        await ((IActorDispatchPort)actorRuntime).DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ForeignScope_WithoutConfirm_DoesNotRevealRegistration()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-foreign", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "reg-foreign",
                Platform = "lark",
                ScopeId = "scope-other",
                NyxChannelBotId = "bot-2",
                NyxAgentApiKeyId = "key-2",
                NyxConversationRouteId = "route-2",
            }));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-foreign"}""");

        json.Should().Contain("not found");
        json.Should().NotContain("confirm_required");
        json.Should().NotContain("scope-other");
        json.Should().NotContain("bot-2");
        json.Should().NotContain("key-2");
        json.Should().NotContain("route-2");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_RequiresScopeContext()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime))
            .BuildServiceProvider();
        var tool = CreateTool(serviceProvider);

        using var scope = PushNyxToken(null);
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1","confirm":true}""");

        json.Should().Contain("scope_id is required");
        await queryPort.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ((IActorDispatchPort)actorRuntime).DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<EventEnvelope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolSource_ReturnsTool_WithTypedDependencies()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllSnapshotsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationSnapshot>>(
                Array.Empty<ChannelBotRegistrationSnapshot>()));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var deprovisioningService = CreateSuccessfulDeprovisioningService();

        var source = new ChannelRegistrationToolSource(
            queryPort,
            commandFacade,
            deprovisioningService);
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("channel_registrations");

        using var scope = PushNyxToken();
        var result = await tools[0].ExecuteAsync("""{"action":"list"}""");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);

        await queryPort.Received(1).QueryAllSnapshotsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var commandFacade = ChannelRegistrationCommandFacadeTestSupport.CreateFacade(actorRuntime, (IActorDispatchPort)actorRuntime);
        var deprovisioningService = CreateSuccessfulDeprovisioningService();

        var missingQuery = () => new ChannelRegistrationTool(null!, commandFacade, deprovisioningService);
        var missingCommand = () => new ChannelRegistrationTool(queryPort, null!, deprovisioningService);
        var missingDeprovisioningService = () => new ChannelRegistrationTool(queryPort, commandFacade, null!);
        var missingSourceQuery = () => new ChannelRegistrationToolSource(null!, commandFacade, deprovisioningService);
        var missingSourceCommand = () => new ChannelRegistrationToolSource(queryPort, null!, deprovisioningService);
        var missingSourceDeprovisioningService = () => new ChannelRegistrationToolSource(queryPort, commandFacade, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingDeprovisioningService.Should().Throw<ArgumentNullException>().WithParameterName("deprovisioningService");
        missingSourceQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingSourceCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandFacade");
        missingSourceDeprovisioningService.Should().Throw<ArgumentNullException>().WithParameterName("deprovisioningService");
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

    private static IDisposable PushNyxToken(string? scopeId = "scope-1") =>
        PushNyxToken(scopeId, scopeId);

    private static IDisposable PushNyxToken(string? scopeId, string? ownerScopeId)
    {
        var previous = AgentToolRequestContext.Current;
        var next = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
            next["scope_id"] = scopeId;
        if (!string.IsNullOrWhiteSpace(ownerScopeId))
            next[LLMRequestMetadataKeys.OwnerScopeId] = ownerScopeId;

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(next);

        return new ResetMetadataScope(previous);
    }

    private static ChannelBotRegistrationEntry NewModelRegistration(
        string registrationId,
        string scopeId,
        string apiKeyId)
    {
        var reference = new SecretReference
        {
            Ref = $"sec-{registrationId}",
            Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
            OwnerScopeKey = scopeId,
            Version = 1,
            Fingerprint = $"sha256:{registrationId}",
            CreatedAtUnixMs = 1788825600000,
        };
        return new ChannelBotRegistrationEntry
        {
            Id = registrationId,
            Platform = "lark",
            ScopeId = scopeId,
            NyxAgentApiKeyId = apiKeyId,
            WorkflowResultDeliveryCredential = reference.Clone(),
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
            ChannelAgentKey = new ChannelAgentKeyCredential
            {
                ApiKeyId = apiKeyId,
                SecretReference = reference,
                Grant = new ChannelAgentKeyGrantSnapshot
                {
                    AllowedServiceIds = { "svc-alpha" },
                    AllowedNodeIds = { "node-alpha" },
                    AllowAllServices = false,
                    AllowAllNodes = false,
                },
            },
        };
    }

    private static ChannelBotRegistrationEntry ExplicitModelRegistration(
        string registrationId,
        string scopeId,
        string apiKeyId,
        params string[] serviceIds)
    {
        var registration = NewModelRegistration(registrationId, scopeId, apiKeyId);
        registration.AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist;
        registration.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist
        {
            ServiceIds = { serviceIds },
        };
        registration.ChannelAgentKey.Grant.ScopePlanDigest =
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        return registration;
    }

    private static ChannelRegistrationTool CreateTool(IServiceProvider? services = null)
    {
        var provider = services ?? CreateDefaultServices().BuildServiceProvider();
        return new ChannelRegistrationTool(
            provider.GetService<IChannelBotRegistrationQueryPort>() ?? Substitute.For<IChannelBotRegistrationQueryPort>(),
            provider.GetService<ChannelRegistrationCommandFacade>() ?? CreateDefaultCommandFacade(),
            provider.GetService<INyxChannelBotDeprovisioningService>() ?? CreateSuccessfulDeprovisioningService());
    }

    private static IServiceCollection CreateDefaultServices()
    {
        return new ServiceCollection()
            .AddSingleton(Substitute.For<IChannelBotRegistrationQueryPort>())
            .AddSingleton(CreateDefaultCommandFacade())
            .AddSingleton(CreateSuccessfulDeprovisioningService());
    }

    private static INyxChannelBotDeprovisioningService CreateSuccessfulDeprovisioningService()
    {
        var service = Substitute.For<INyxChannelBotDeprovisioningService>();
        service.DeprovisionAsync(
                Arg.Any<string>(),
                Arg.Any<NyxChannelBotDeprovisioningRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxChannelBotDeprovisioningResult(
                ChannelBotRemoved: true,
                AgentKeyRemoved: true,
                Succeeded: true,
                Warnings: Array.Empty<string>())));
        return service;
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
