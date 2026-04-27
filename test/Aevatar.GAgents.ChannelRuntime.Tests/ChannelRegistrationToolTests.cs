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

public sealed class ChannelRegistrationToolTests
{
    [Fact]
    public void Metadata_ReflectsRelayOnlyContract()
    {
        var tool = new ChannelRegistrationTool(new ServiceCollection().BuildServiceProvider());

        tool.Name.Should().Be("channel_registrations");
        tool.Description.Should().Contain("register_lark_via_nyx");
        tool.Description.Should().Contain("rebuild_projection");
        tool.Description.Should().Contain("repair_lark_mirror");
        JsonDocument.Parse(tool.ParametersSchema).RootElement
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .Equal("list", "register_lark_via_nyx", "rebuild_projection", "repair_lark_mirror", "delete");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenNoNyxTokenIsAvailable()
    {
        AgentToolRequestContext.CurrentMetadata = null;
        try
        {
            var tool = new ChannelRegistrationTool(new ServiceCollection().BuildServiceProvider());

            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            result.Should().Contain("No NyxID access token available");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
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
        var tool = new ChannelRegistrationTool(serviceProvider);

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
        var tool = new ChannelRegistrationTool(serviceProvider);

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
    public async Task ExecuteAsync_RebuildProjection_DispatchesRefreshCommand()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                    NyxAgentApiKeyId = "key-1",
                },
            ]));

        List<EventEnvelope> capturedEnvelopes = [];
        var actor = Substitute.For<IActor>();
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(actor));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelopes.Add(envelope)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var verifier = new RecordingOwnershipVerifier();

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .AddSingleton<INyxRelayApiKeyOwnershipVerifier>(verifier)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"rebuild_projection","reason":"manual-debug","registration_id":"reg-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("observed_registrations_before_rebuild").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("empty_scope_registrations_backfilled").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("backfill_status").GetString().Should().Be("dispatched");
        doc.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0);
        capturedEnvelopes.Should().HaveCount(2);
        var repair = capturedEnvelopes[0].Payload.Unpack<ChannelBotRepairScopeIdCommand>();
        repair.RegistrationId.Should().Be("reg-1");
        repair.ScopeId.Should().Be("scope-1");
        capturedEnvelopes[1].Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("manual-debug");
        verifier.Calls.Should().ContainSingle()
            .Which.Should().Be(("test-token", "scope-1", "key-1"));
    }

    [Fact]
    public async Task ExecuteAsync_RebuildProjection_DoesNotBackfillEmptyScopeRegistrationWithoutSelector()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                },
            ]));

        List<EventEnvelope> capturedEnvelopes = [];
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelopes.Add(envelope)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"rebuild_projection","reason":"manual-debug"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("observed_registrations_before_rebuild").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("empty_scope_registrations_observed").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("empty_scope_registrations_backfilled").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("backfill_status").GetString().Should().Be("skipped");
        doc.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .ContainSingle(message => message != null && message.Contains("pass registration_id", StringComparison.Ordinal));
        doc.RootElement.GetProperty("note").GetString().Should().Contain("pass registration_id");
        capturedEnvelopes.Should().HaveCount(1);
        capturedEnvelopes[0].Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("manual-debug");
    }

    [Fact]
    public async Task ExecuteAsync_RebuildProjection_ReportsUnavailable_WhenQuerySideThrows()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ChannelBotRegistrationEntry>>(
                new InvalidOperationException("projection reader unavailable")));

        List<EventEnvelope> capturedEnvelopes = [];
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelopes.Add(envelope)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"rebuild_projection","reason":"manual-debug"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("backfill_status").GetString().Should().Be("unavailable");
        doc.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .ContainSingle(message => message != null && message.Contains("projection reader unavailable", StringComparison.Ordinal));
        capturedEnvelopes.Should().ContainSingle();
        capturedEnvelopes[0].Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("manual-debug");
    }

    [Fact]
    public async Task ExecuteAsync_RebuildProjection_DoesNotBackfillEmptyScopeRegistrationsWhenForceHasNoSelector()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                    NyxAgentApiKeyId = "key-1",
                },
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-2",
                    Platform = "lark",
                    NyxAgentApiKeyId = "key-2",
                },
            ]));

        List<EventEnvelope> capturedEnvelopes = [];
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelopes.Add(envelope)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var verifier = new RecordingOwnershipVerifier();

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .AddSingleton<INyxRelayApiKeyOwnershipVerifier>(verifier)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"rebuild_projection","reason":"manual-debug","force":true}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("empty_scope_registrations_observed").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("empty_scope_registrations_backfilled").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("note").GetString().Should().Contain("force=true only applies");
        capturedEnvelopes.Should().HaveCount(1);
        capturedEnvelopes[0].Payload.Unpack<ChannelBotRebuildProjectionCommand>().Reason.Should().Be("manual-debug");
        verifier.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RebuildProjection_DispatchesEvenWhenQueryObservationFails()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ChannelBotRegistrationEntry>>(new InvalidOperationException("projection reader unavailable")));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"rebuild_projection","reason":"manual-debug"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("observed_registrations_before_rebuild").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("backfill_status").GetString().Should().Be("unavailable");
        doc.RootElement.GetProperty("note").GetString().Should().Contain("backfill outcome could not be decided");
        capturedEnvelope.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_RepairLarkMirror_ReturnsMirrorRepairResult()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([]));

        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.RepairLocalMirrorAsync(Arg.Any<NyxLarkMirrorRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkMirrorRepairResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: "reg-restore-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"repair_lark_mirror","registration_id":"reg-restore-1","webhook_base_url":"https://aevatar.example.com","nyx_channel_bot_id":"bot-1","nyx_agent_api_key_id":"key-1","nyx_conversation_route_id":"route-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-restore-1");
        await provisioningService.Received(1).RepairLocalMirrorAsync(
            Arg.Is<NyxLarkMirrorRepairRequest>(request =>
                request.AccessToken == "test-token" &&
                request.RequestedRegistrationId == "reg-restore-1" &&
                request.ScopeId == "scope-1" &&
                request.WebhookBaseUrl == "https://aevatar.example.com" &&
                request.NyxChannelBotId == "bot-1" &&
                request.NyxAgentApiKeyId == "key-1" &&
                request.NyxConversationRouteId == "route-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepairLarkMirror_StillRepairsWhenQuerySideIsUnavailable()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ChannelBotRegistrationEntry>>(new InvalidOperationException("projection reader unavailable")));

        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.RepairLocalMirrorAsync(Arg.Any<NyxLarkMirrorRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkMirrorRepairResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: "reg-restore-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"repair_lark_mirror","registration_id":"reg-restore-1","webhook_base_url":"https://aevatar.example.com","nyx_channel_bot_id":"bot-1","nyx_agent_api_key_id":"key-1","nyx_conversation_route_id":"route-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        await provisioningService.Received(1).RepairLocalMirrorAsync(
            Arg.Is<NyxLarkMirrorRepairRequest>(request =>
                request.RequestedRegistrationId == "reg-restore-1" &&
                request.ScopeId == "scope-1" &&
                request.NyxChannelBotId == "bot-1" &&
                request.NyxAgentApiKeyId == "key-1" &&
                request.NyxConversationRouteId == "route-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepairLarkMirror_DoesNotShortCircuitOnPartialNyxIdentityMatch()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-stale",
                    Platform = "lark",
                    NyxProviderSlug = "api-lark-bot",
                    WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1",
                    NyxChannelBotId = "bot-1",
                    NyxAgentApiKeyId = "key-stale",
                    NyxConversationRouteId = "route-stale",
                },
            ]));

        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.RepairLocalMirrorAsync(Arg.Any<NyxLarkMirrorRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkMirrorRepairResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: "reg-restore-1",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"repair_lark_mirror","registration_id":"reg-restore-1","webhook_base_url":"https://aevatar.example.com","nyx_channel_bot_id":"bot-1","nyx_agent_api_key_id":"key-1","nyx_conversation_route_id":"route-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-restore-1");
        await provisioningService.Received(1).RepairLocalMirrorAsync(
            Arg.Is<NyxLarkMirrorRepairRequest>(request =>
                request.ScopeId == "scope-1" &&
                request.NyxChannelBotId == "bot-1" &&
                request.NyxAgentApiKeyId == "key-1" &&
                request.NyxConversationRouteId == "route-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RepairLarkMirror_BackfillsExistingEmptyScopeMirror()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-empty-scope",
                    Platform = "lark",
                    NyxProviderSlug = "api-lark-bot",
                    WebhookUrl = "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1",
                    NyxChannelBotId = "bot-1",
                    NyxAgentApiKeyId = "key-1",
                    NyxConversationRouteId = "route-1",
                },
            ]));

        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        provisioningService.RepairLocalMirrorAsync(Arg.Any<NyxLarkMirrorRepairRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new NyxLarkMirrorRepairResult(
                Succeeded: true,
                Status: "accepted",
                RegistrationId: "reg-empty-scope",
                NyxChannelBotId: "bot-1",
                NyxAgentApiKeyId: "key-1",
                NyxConversationRouteId: "route-1",
                WebhookUrl: "https://nyx.example.com/api/v1/webhooks/channel/lark/bot-1")));

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync(
            """{"action":"repair_lark_mirror","webhook_base_url":"https://aevatar.example.com","nyx_channel_bot_id":"bot-1","nyx_agent_api_key_id":"key-1","nyx_conversation_route_id":"route-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("registration_id").GetString().Should().Be("reg-empty-scope");
        await provisioningService.Received(1).RepairLocalMirrorAsync(
            Arg.Is<NyxLarkMirrorRepairRequest>(request =>
                request.RequestedRegistrationId == "reg-empty-scope" &&
                request.ScopeId == "scope-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RegisterLarkViaNyx_RejectsMissingScopeContext()
    {
        var provisioningService = Substitute.For<INyxLarkProvisioningService>();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(provisioningService)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken(null);
        var json = await tool.ExecuteAsync(
            """{"action":"register_lark_via_nyx","app_id":"cli_123","app_secret":"secret","webhook_base_url":"https://aevatar.example.com"}""");

        json.Should().Contain("scope_id is required");
        await provisioningService.DidNotReceive().ProvisionAsync(Arg.Any<NyxLarkProvisioningRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateToken_ReturnsRetiredError()
    {
        var tool = new ChannelRegistrationTool(new ServiceCollection().BuildServiceProvider());

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
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1"}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("confirm_required");
        doc.RootElement.GetProperty("registration_mode").GetString().Should().Be("nyx_relay_webhook");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_WithConfirm_DispatchesUnregisterCommand()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
                {
                    Id = "reg-1",
                    Platform = "lark",
                }),
                Task.FromResult<ChannelBotRegistrationEntry?>(null));

        EventEnvelope? capturedEnvelope = null;
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Do<EventEnvelope>(envelope => capturedEnvelope = envelope),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1","confirm":true}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");
        capturedEnvelope.Should().NotBeNull();
        capturedEnvelope!.Payload.Unpack<ChannelBotUnregisterCommand>().RegistrationId.Should().Be("reg-1");
    }

    [Fact]
    public async Task ExecuteAsync_Delete_WithConfirm_ReturnsAccepted_WhenProjectionStillShowsRegistration()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "reg-1",
            Platform = "lark",
        };

        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));

        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            .Returns(Task.FromResult<IActor?>(Substitute.For<IActor>()));
        ((IActorDispatchPort)actorRuntime).DispatchAsync(
                ChannelBotRegistrationGAgent.WellKnownId,
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(actorRuntime)
            .AddSingleton((IActorDispatchPort)actorRuntime)
            .BuildServiceProvider();
        var tool = new ChannelRegistrationTool(serviceProvider);

        using var scope = PushNyxToken();
        var json = await tool.ExecuteAsync("""{"action":"delete","registration_id":"reg-1","confirm":true}""");
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("note").GetString().Should().Contain("projection not yet confirmed");
    }

    private static IDisposable PushNyxToken(string? scopeId = "scope-1")
    {
        var previous = AgentToolRequestContext.CurrentMetadata;
        var next = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
            next["scope_id"] = scopeId;

        AgentToolRequestContext.CurrentMetadata = next;

        return new ResetMetadataScope(previous);
    }

    private sealed class RecordingOwnershipVerifier : INyxRelayApiKeyOwnershipVerifier
    {
        public List<(string AccessToken, string ExpectedScopeId, string NyxAgentApiKeyId)> Calls { get; } = [];

        public Task<NyxRelayApiKeyOwnershipVerification> VerifyAsync(
            string accessToken,
            string expectedScopeId,
            string nyxAgentApiKeyId,
            CancellationToken ct)
        {
            Calls.Add((accessToken, expectedScopeId, nyxAgentApiKeyId));
            return Task.FromResult(new NyxRelayApiKeyOwnershipVerification(true, "verified"));
        }
    }

    private sealed class ResetMetadataScope(IReadOnlyDictionary<string, string>? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.CurrentMetadata = previous;
    }
}
