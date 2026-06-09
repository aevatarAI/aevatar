using System.Net;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChannelRuntime.Tests.Identity;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.WorkflowDraftRun;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelConversationTurnRunnerTests
{
    [Theory]
    [InlineData("/workflow run daily-greeting")]
    [InlineData("/run-workflow daily-greeting")]
    public async Task RunInboundAsync_ShouldRequestWorkflowDraftRun_ForExplicitWorkflowIntent(string text)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var services = BuildAgentBuilderToolServices(new StubScopeWorkflowQueryPort(BuildWorkflowSummary("scope-1", "daily-greeting")));
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                text,
                "msg-workflow-1",
                transportExtras: new TransportExtras
                {
                    NyxRegistrationScopeId = "scope-1",
                    NyxUserAccessToken = "user-token-1",
                }),
            RelayRuntimeContext(
                "msg-workflow-1",
                nyxUserAccessToken: "user-token-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkflowDraftRunRequest.Should().NotBeNull();
        result.LlmReplyRequest.Should().BeNull();
        result.WorkflowDraftRunRequest!.WorkflowSource.WorkflowId.Should().Be("daily-greeting");
        result.WorkflowDraftRunRequest.WorkflowSource.DefinitionActorId.Should().Be("actor-daily-greeting");
        result.WorkflowDraftRunRequest.RunId.Should().StartWith("workflow-draft-run-");
        result.WorkflowDraftRunRequest.TargetActorId.Should().BeEmpty();
        result.WorkflowDraftRunRequest.NyxUserAccessToken.Should().Be("user-token-1");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldReplyAndNotFallbackToLlm_WhenWorkflowTokenMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var services = BuildAgentBuilderToolServices(new StubScopeWorkflowQueryPort(BuildWorkflowSummary("scope-1", "daily-greeting")));
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/workflow run daily-greeting",
                "msg-workflow-token-missing",
                transportExtras: new TransportExtras
                {
                    NyxRegistrationScopeId = "scope-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkflowDraftRunRequest.Should().BeNull();
        result.LlmReplyRequest.Should().BeNull();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("NyxID");
    }

    [Theory]
    [InlineData("aevatar_start_workflow daily-greeting")]
    [InlineData("跑一下 daily-greeting 的 workflow")]
    [InlineData("run daily-greeting workflow")]
    public async Task RunInboundAsync_ShouldUseNormalLlmPath_ForUnregisteredWorkflowText(string text)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var services = BuildAgentBuilderToolServices(new StubScopeWorkflowQueryPort(BuildWorkflowSummary("scope-1", "daily-greeting")));
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                text,
                "msg-workflow-tool-name",
                transportExtras: new TransportExtras
                {
                    NyxRegistrationScopeId = "scope-1",
                    NyxUserAccessToken = "user-token-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkflowDraftRunRequest.Should().BeNull();
        result.LlmReplyRequest.Should().NotBeNull();
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRequestDeferredLlmReply_ForNormalMessage()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-1", ConversationScope.Group, "oc_group_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.CorrelationId.Should().Be("msg-1");
        result.LlmReplyRequest.RunId.Should().StartWith("agent-run-");
        result.LlmReplyRequest.RunId.Should().NotBe(result.LlmReplyRequest.CorrelationId);
        result.LlmReplyRequest.TargetActorId.Should().BeEmpty();
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.ChatType].Should().Be("group");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldIncludePlatformMessageIdInLlmMetadata_WhenAvailable()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_123",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.PlatformMessageId].Should().Be("om_123");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldIncludeLarkStableIdsInLlmMetadata_WhenAvailable()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-lark-stable-ids",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxPlatformMessageId = "om_123",
                    NyxLarkUnionId = "on_union_1",
                    NyxLarkChatId = "oc_chat_1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.PlatformMessageId].Should().Be("om_123");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.LarkUnionId].Should().Be("on_union_1");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.LarkChatId].Should().Be("oc_chat_1");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldUseLarkApprovalOperatorUserIdInLlmMetadata_WhenAvailable()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-lark-operator-ids",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxSenderUserId = "nyx-user-1",
                    NyxLarkOperatorUserId = "lark-user-1",
                    NyxLarkOperatorOpenId = "ou_open_operator_1",
                    NyxLarkOperatorUnionId = "on_operator_1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.LarkOperatorUserId].Should().Be("lark-user-1");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.LarkOperatorUserId].Should().NotBe("nyx-user-1");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.LarkOperatorOpenId].Should().Be("ou_open_operator_1");
        result.LlmReplyRequest.Metadata[ChannelMetadataKeys.LarkOperatorUnionId].Should().Be("on_operator_1");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldApplyOwnerUserConfigOverridesToLlmControl_WhenSourceRegistered()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var ownerSource = new StubOwnerLlmConfigSource(
            new OwnerLlmConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
                MaxToolRounds: 12));
        var services = new ServiceCollection()
            .AddSingleton<IOwnerLlmConfigSource>(ownerSource)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-owner-cfg-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        var llmControl = LLMControlContextMapper.FromPayload(result.LlmReplyRequest!.LlmControl);
        llmControl.ModelOverride.Should().Be("gpt-5.5");
        llmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        llmControl.MaxToolRoundsOverride.Should().Be(12);
        ownerSource.Calls.Should().ContainSingle();
        ownerSource.Calls[0].Should().Be("scope-1");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldFallThroughToProviderDefaults_WhenOwnerLlmConfigSourceNotRegistered()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-owner-cfg-2"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride);
    }

    [Fact]
    public async Task RunInboundAsync_ShouldFallThroughOnOwnerConfigLookupFailure_WithoutFailingTurn()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var ownerSource = new StubOwnerLlmConfigSource(throwOnGet: true);
        var services = new ServiceCollection()
            .AddSingleton<IOwnerLlmConfigSource>(ownerSource)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-owner-cfg-3"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSkipOwnerConfigOverrides_WhenSpecificFieldsEmpty()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var ownerSource = new StubOwnerLlmConfigSource(
            new OwnerLlmConfig(
                DefaultModel: null,
                PreferredLlmRoute: "/api/v1/proxy/s/chrono-llm",
                MaxToolRounds: 0));
        var services = new ServiceCollection()
            .AddSingleton<IOwnerLlmConfigSource>(ownerSource)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-owner-cfg-4"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        var llmControl = LLMControlContextMapper.FromPayload(result.LlmReplyRequest!.LlmControl);
        llmControl.ModelOverride.Should().BeNull();
        llmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
        llmControl.MaxToolRoundsOverride.Should().BeNull();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSendImmediateLarkReaction_WhenRelayTurnProvidesPlatformMessageId()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = "user-token-1",
                    NyxPlatformMessageId = "om_123",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        nyxHandler.Requests.Should().ContainSingle();
        nyxHandler.Requests[0].Path.Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_123/reactions");
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer user-token-1");
        nyxHandler.Requests[0].Body.Should().Contain("\"emoji_type\":\"Typing\"");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldNotAwaitImmediateLarkReaction()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new BlockingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var runTask = runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = "user-token-1",
                    NyxPlatformMessageId = "om_123",
                }),
            CancellationToken.None);

        await nyxHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        runTask.IsCompleted.Should().BeTrue();

        var result = await runTask;
        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();

        nyxHandler.Release.TrySetResult();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSkipImmediateLarkReaction_WhenPlatformMessageIdIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = "user-token-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldResolveRegistrationByNyxAgentApiKeyId_WhenBotIdDoesNotMatch()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("missing-reg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(BuildRegistrationEntry()));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-1",
                botId: "missing-reg",
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxMessageId = "nyx-msg-1",
                    NyxPlatform = "lark",
                    NyxConversationId = "nyx-conv-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.RegistrationId.Should().Be("reg-1");
        result.LlmReplyRequest.Activity.TransportExtras?.NyxAgentApiKeyId.Should().Be("nyx-key-1");
        await registrationByNyxIdentityPort.Received(1)
            .GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>());
        await registrationQueryPort.DidNotReceive()
            .GetAsync("missing-reg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldFallbackToBoundedRegistrationScan_ForRelayTurnWhenIdentityQueryMisses()
    {
        var registration = BuildRegistrationEntry();
        registration.NyxAgentApiKeyId = "nyx-key-1";
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([registration]));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-scan-1",
                botId: "nyx-key-1",
                outboundDelivery: new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-relay-1",
                },
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxMessageId = "nyx-msg-1",
                    NyxPlatform = "lark",
                    NyxConversationId = "nyx-conv-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.RegistrationId.Should().Be("reg-1");
        await registrationByNyxIdentityPort.Received(1)
            .GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>());
        await registrationQueryPort.Received(1)
            .QueryAllAsync(Arg.Any<CancellationToken>());
        await registrationQueryPort.DidNotReceive()
            .GetAsync("nyx-key-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_BoundedRegistrationScan_ReturnsFirstMatchWhenMultipleShareNyxKey()
    {
        var stale = BuildRegistrationEntry("reg-old");
        stale.NyxAgentApiKeyId = "nyx-key-1";
        var fresh = BuildRegistrationEntry("reg-new");
        fresh.NyxAgentApiKeyId = "nyx-key-1";

        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([stale, fresh]));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-dup-1",
                botId: "nyx-key-1",
                outboundDelivery: new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-relay-1",
                },
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxPlatform = "lark",
                }),
            CancellationToken.None);

        // Bounded scan has no ordering guarantee across duplicates sharing NyxAgentApiKeyId
        // and returns the first hit from the projection. The stale entry can shadow the live one.
        result.Success.Should().BeTrue();
        result.LlmReplyRequest!.RegistrationId.Should().Be("reg-old");
    }

    [Fact]
    public async Task RunInboundAsync_BoundedRegistrationScan_FallsThroughToBotIdLookup_WhenScanMisses()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry
                {
                    Id = "reg-other",
                    NyxAgentApiKeyId = "different-key",
                },
            ]));
        registrationQueryPort.GetAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-miss-scan-1",
                botId: "nyx-key-1",
                outboundDelivery: new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-relay-1",
                },
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxPlatform = "lark",
                }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("registration_not_found");
        await registrationQueryPort.Received(1).QueryAllAsync(Arg.Any<CancellationToken>());
        await registrationQueryPort.Received(1).GetAsync("nyx-key-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSkipBoundedRegistrationScan_WhenOutboundDeliveryMissing()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello without relay",
                "msg-no-delivery-1",
                botId: "nyx-key-1",
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxPlatform = "lark",
                }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("registration_not_found");
        await registrationQueryPort.DidNotReceive().QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSkipBoundedRegistrationScan_ForNonRelayIdentityMiss()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("missing-reg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var registrationByNyxIdentityPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        registrationByNyxIdentityPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            registrationQueryByNyxIdentityPort: registrationByNyxIdentityPort);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello from relay",
                "msg-nyx-miss-1",
                botId: "missing-reg",
                transportExtras: new TransportExtras
                {
                    NyxAgentApiKeyId = "nyx-key-1",
                    NyxMessageId = "nyx-msg-1",
                    NyxPlatform = "lark",
                    NyxConversationId = "nyx-conv-1",
                }),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("registration_not_found");
        await registrationQueryPort.DidNotReceive()
            .QueryAllAsync(Arg.Any<CancellationToken>());
        await registrationQueryPort.Received(1)
            .GetAsync("missing-reg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldMapChannelScopeChatTypeExhaustively()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("announce", "msg-channel-1", ConversationScope.Channel, "oc_channel_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata[ChannelMetadataKeys.ChatType].Should().Be("channel");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldReturnTransientFailure_WhenWorkflowResumeServiceIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1", "msg-resume-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("workflow_resume_service_unavailable");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldReturnWorkflowReceipt_WhenResumeCommandIsAccepted()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-1", "corr-1")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1 comment='ship it'", "msg-resume-2"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("workflow-resume:cmd-1");
        adapter.Replies.Should().BeEmpty();
        dispatchService.Commands.Should().ContainSingle();
        dispatchService.Commands[0].ActorId.Should().Be("actor-1");
        dispatchService.Commands[0].RunId.Should().Be("run-1");
        dispatchService.Commands[0].StepId.Should().Be("step-1");
        dispatchService.Commands[0].Approved.Should().BeTrue();
        dispatchService.Commands[0].Feedback.Should().Be("ship it");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteCardActionResume_WhenCardPayloadContainsWorkflowIds()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt("actor-1", "run-1", "cmd-card-1", "corr-card-1")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var activity = BuildCardActionActivity("evt-card-1");
        activity.Content.CardAction.ActionKind = ActionElementKind.FormSubmit;
        activity.Content.CardAction.WorkflowResume = new WorkflowResumeActionPayload
        {
            ActorId = "actor-1",
            RunId = "run-1",
            StepId = "approval-1",
            Approved = false,
            UserInput = "Need stronger hook",
        };

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("workflow-resume:cmd-card-1");
        adapter.Replies.Should().BeEmpty();
        dispatchService.Commands.Should().ContainSingle();
        dispatchService.Commands[0].ActorId.Should().Be("actor-1");
        dispatchService.Commands[0].RunId.Should().Be("run-1");
        dispatchService.Commands[0].StepId.Should().Be("approval-1");
        dispatchService.Commands[0].Approved.Should().BeFalse();
        dispatchService.Commands[0].Feedback.Should().Be("Need stronger hook");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteAgentBuilderCardAction_WhenCardPayloadCarriesAgentBuilderAction()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var activity = BuildCardActionActivity("evt-card-builder-1");
        activity.Content.CardAction.Arguments["agent_builder_action"] = "list_agents";

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:evt-card-builder-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.ChatType.Should().Be("card_action");
        adapter.Replies[0].Inbound.Extra.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("list_agents");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteAgentBuilderCardAction_WhenActionIdCarriesAgentBuilderAction()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var activity = BuildCardActionActivity("evt-card-builder-action-id-1");
        activity.Content.CardAction.ActionId = "list_agents";

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:evt-card-builder-action-id-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.Extra.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("list_agents");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldIgnoreCardAction_WhenNeitherWorkflowNorAgentBuilderMatches()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        // No agent_builder_action, no actor_id/run_id/step_id — an unknown card submit
        // that must not become an LLM turn.
        var activity = BuildCardActionActivity(
            "evt-card-unknown-1",
            ("unrelated_field", "value"));

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("ignored:unrecognized_card_action:evt-card-unknown-1");
        result.LlmReplyRequest.Should().BeNull("unrecognized card_action must not trigger an LLM turn");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldContinueGenericFormSubmitToLlm_WhenTypedFormSubmitCarriesFields()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);
        var activity = BuildCardActionActivity(
            "evt-card-form-submit-1",
            ("environment", "prod"),
            ("reason", "deploy ready"));
        activity.Content.CardAction.ActionKind = ActionElementKind.FormSubmit;

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.SentActivityId.Should().BeEmpty();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Be("environment: prod\nreason: deploy ready");
        result.LlmReplyRequest.Activity.Content.CardAction.ActionKind.Should().Be(ActionElementKind.FormSubmit);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldIgnoreCardAction_WhenFormFieldsExistButActionKindIsNotFormSubmit()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);
        var activity = BuildCardActionActivity(
            "evt-card-select-fields-1",
            ("environment", "prod"));
        activity.Content.CardAction.ActionKind = ActionElementKind.Select;

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("ignored:unrecognized_card_action:evt-card-select-fields-1");
        result.LlmReplyRequest.Should().BeNull();
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteLlmSelectionCardAction_WhenPayloadCarriesLlmAction()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "scope-1",
            ExternalUserId = "ou_user_1",
        };
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(subject, new BindingId { Value = "bnd-user-1" });
        var option = new UserLlmOption(
            ServiceId: "svc-openai",
            ServiceSlug: "openai-work",
            DisplayName: "OpenAI Work",
            RouteValue: "/api/v1/proxy/s/openai-work",
            DefaultModel: "gpt-5.4",
            AvailableModels: ["gpt-5.4"],
            Status: "ready",
            Source: "user",
            Allowed: true,
            Description: null);
        var optionsService = new StubUserLlmOptionsService(option);
        var selectionService = new RecordingUserLlmSelectionService();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<IUserLlmOptionsService>(optionsService)
            .AddSingleton<IUserLlmSelectionService>(selectionService)
            .AddSingleton<IUserLlmOptionsRenderer<MessageContent>>(new TextUserLlmOptionsRenderer())
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);
        var activity = BuildCardActionActivity("evt-llm-select-1");
        activity.Content.CardAction.LlmSelection = new LlmSelectionActionPayload
        {
            Action = TextUserLlmOptionsRenderer.SelectServiceAction,
            ServiceId = "svc-openai",
        };

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().BeNull();
        result.SentActivityId.Should().Be("direct-reply:evt-llm-select-1");
        selectionService.SelectedServiceId.Should().Be("svc-openai");
        selectionService.Context?.BindingId.Value.Should().Be("bnd-user-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("OpenAI Work");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldHandleCompactLlmCardAction_WithSubmittedValue()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "scope-1",
            ExternalUserId = "ou_user_1",
        };
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(subject, new BindingId { Value = "bnd-user-1" });
        var option = new UserLlmOption(
            ServiceId: "123e4567-e89b-12d3-a456-426614174000",
            ServiceSlug: "openai-work",
            DisplayName: "OpenAI Work",
            RouteValue: "/api/v1/proxy/s/openai-work",
            DefaultModel: "gpt-5.4",
            AvailableModels: ["gpt-5.4"],
            Status: "ready",
            Source: "user",
            Allowed: true,
            Description: null);
        var optionsService = new StubUserLlmOptionsService(option);
        var selectionService = new RecordingUserLlmSelectionService();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<IUserLlmOptionsService>(optionsService)
            .AddSingleton<IUserLlmSelectionService>(selectionService)
            .AddSingleton<IUserLlmOptionsRenderer<MessageContent>>(new TextUserLlmOptionsRenderer())
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);
        var activity = BuildCardActionActivity("evt-llm-select-compact-1");
        activity.Content.CardAction!.ActionId = TextUserLlmOptionsRenderer.SelectServiceActionId;
        activity.Content.CardAction.SubmittedValue = option.ServiceId;

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        selectionService.SelectedServiceId.Should().Be(option.ServiceId);
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("OpenAI Work");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldApplyTypedLlmPreset_WhenPayloadCarriesPresetId()
    {
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "scope-1",
            ExternalUserId = "ou_user_1",
        };
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(subject, new BindingId { Value = "bnd-user-1" });
        var option = new UserLlmOption(
            ServiceId: "svc-openai",
            ServiceSlug: "openai-work",
            DisplayName: "OpenAI Work",
            RouteValue: "/api/v1/proxy/s/openai-work",
            DefaultModel: "gpt-5.4",
            AvailableModels: ["gpt-5.4"],
            Status: "ready",
            Source: "user",
            Allowed: true,
            Description: null);
        var optionsService = new StubUserLlmOptionsService(option);
        var selectionService = new RecordingUserLlmSelectionService();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<IUserLlmOptionsService>(optionsService)
            .AddSingleton<IUserLlmSelectionService>(selectionService)
            .AddSingleton<IUserLlmOptionsRenderer<MessageContent>>(new TextUserLlmOptionsRenderer())
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);
        var activity = BuildCardActionActivity("evt-llm-preset-typed-1");
        activity.Content.CardAction.LlmSelection = new LlmSelectionActionPayload
        {
            Action = TextUserLlmOptionsRenderer.ApplyPresetAction,
            PresetId = "work-fast",
        };

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().BeNull();
        result.SentActivityId.Should().Be("direct-reply:evt-llm-preset-typed-1");
        selectionService.PresetId.Should().Be("work-fast");
        selectionService.Context?.BindingId.Value.Should().Be("bnd-user-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("work-fast");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldMapWorkflowResumeValidationErrors()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidStepId("actor-1", "run-1", " ")),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1", "msg-resume-3"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_step_id");
        result.ErrorSummary.Should().Contain("stepId");
        adapter.Replies.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(WorkflowResumeDispatchErrors))]
    public async Task RunInboundAsync_ShouldMapWorkflowResumeDispatchErrors(
        WorkflowRunControlStartError error,
        string expectedCode,
        FailureKind expectedKind)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var dispatchService = new RecordingWorkflowResumeDispatchService
        {
            Result = CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Failure(error),
        };
        var services = new ServiceCollection()
            .AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(dispatchService)
            .BuildServiceProvider();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/approve actor_id=actor-1 run_id=run-1 step_id=step-1", "msg-resume-error"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedCode);
        result.FailureKind.Should().Be(expectedKind);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteGoalSlashCommandToOrnnSkillDiscovery()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-unexpected"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/goal ship command fix",
                "msg-goal-relay-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-goal-1",
                    CorrelationId = "corr-goal-relay-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-goal-relay-1",
                "relay-token-goal-1",
                "relay-msg-goal-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.ReplyToken.Should().Be("relay-token-goal-1");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("Ornn skill-backed command");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("use_skill");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("goal");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("ship command fix");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("/goal ship command fix");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("goal");
        recovery.OriginalCommand.Should().Be("/goal ship command fix");
        recovery.PrimarySkillName.Should().Be("goal");
        recovery.CommandArguments.Should().Be("ship command fix");
        recovery.DiscoveryRequested.Should().BeFalse();
        recovery.MaxOrnnSearchAttempts.Should().Be(2);
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteSlashAliasThroughGenericSkillRecovery()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-unexpected"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily alice",
                "msg-generic-daily-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-missing-token-1",
                    CorrelationId = "corr-missing-token-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-missing-token-1",
                "relay-token-daily-1",
                "relay-msg-missing-token-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.ReplyToken.Should().Be("relay-token-daily-1");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("Ornn skill-backed command");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("use_skill");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("daily");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("alice");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("/daily alice");
        result.LlmReplyRequest.Activity.Content.Text.Should().NotContain("chrono-ai-daily");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.CommandName.Should().Be("daily");
        recovery.PrimarySkillName.Should().Be("daily");
        recovery.CommandArguments.Should().Be("alice");
        recovery.OriginalCommand.Should().Be("/daily alice");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteCanonicalSkillTriggerAcrossChannels()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "::Goal ship command fix",
                "msg-canonical-skill-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "telegram",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Contain("`::goal`");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("use_skill");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("ship command fix");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("goal");
        recovery.PrimarySkillName.Should().Be("goal");
        recovery.CommandArguments.Should().Be("ship command fix");
        recovery.OriginalCommand.Should().Be("::Goal ship command fix");
        recovery.DiscoveryRequested.Should().BeFalse();
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldAttachDiscoveryRecoveryForBareCanonicalTrigger()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "::",
                "msg-canonical-discovery-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "telegram",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Be("::");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeFalse();
        recovery.CommandName.Should().BeNull();
        recovery.PrimarySkillName.Should().BeNull();
        recovery.CommandArguments.Should().BeNull();
        recovery.OriginalCommand.Should().Be("::");
        recovery.DiscoveryRequested.Should().BeTrue();
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldTreatCanonicalModelTriggerAsSkillInvocation()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "::model status",
                "msg-canonical-model-skill-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Contain("`::model`");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.CommandName.Should().Be("model");
        recovery.PrimarySkillName.Should().Be("model");
        recovery.CommandArguments.Should().Be("status");
        recovery.OriginalCommand.Should().Be("::model status");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldCarryRelayReplyToken_WhenNormalRelayTextFallsBackToLlm()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-normal-relay-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-normal-1",
                    CorrelationId = "corr-normal-relay-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-normal-relay-1",
                "relay-token-normal-1",
                "relay-msg-normal-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.ReplyToken.Should().Be("relay-token-normal-1");
        result.LlmReplyRequest.ReplyTokenExpiresAtUnixMs.Should().BeGreaterThan(0);
        result.LlmReplyRequest.Activity.OutboundDelivery.ReplyMessageId.Should().Be("relay-msg-normal-1");
        result.LlmReplyRequest.Activity.OutboundDelivery.CorrelationId.Should().Be("corr-normal-relay-1");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldNotRouteNonSlashAgentBuilderIntentToToolExecution()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-unexpected"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "list agents",
                "msg-normal-agent-builder-intent-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-normal-agent-builder-intent-1",
                    CorrelationId = "corr-normal-agent-builder-intent-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-normal-agent-builder-intent-1",
                "relay-token-normal-agent-builder-intent-1",
                "relay-msg-normal-agent-builder-intent-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.ReplyToken.Should().Be("relay-token-normal-agent-builder-intent-1");
        result.LlmReplyRequest.Activity.Content.Text.Should().Be("list agents");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldUseRuntimeNyxToken_ForSanitizedRelayAgentBuilderTurn()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-agent-builder-reply"}""");
        var callerScopeResolver = new CapturingCallerScopeResolver();
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>())
            .AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>())
            .AddSingleton(Substitute.For<ISkillRunnerCommandPort>())
            .AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>())
            .AddSingleton<ICallerScopeResolver>(callerScopeResolver)
            .AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory(new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://example.com" },
                new HttpClient(new RecordingJsonHandler("""{"ok":true}"""))
                {
                    BaseAddress = new Uri("https://example.com"),
                })))
            .BuildServiceProvider();
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            services,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/agents",
                "msg-runtime-token-agent-builder-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-runtime-token-1",
                    CorrelationId = "corr-runtime-token-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                    NyxUserAccessToken = string.Empty,
                }),
            RelayRuntimeContext(
                "corr-runtime-token-1",
                replyToken: "relay-token-runtime-1",
                replyMessageId: "relay-msg-runtime-token-1",
                nyxUserAccessToken: "runtime-user-token-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-runtime-token-agent-builder-1");
        result.OutboundDelivery?.ReplyMessageId.Should().Be("relay-msg-runtime-token-1");
        result.OutboundDelivery?.CorrelationId.Should().Be("corr-runtime-token-1");
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-runtime-1");
        callerScopeResolver.CapturedMetadata.Should().NotBeNull();
        callerScopeResolver.CapturedMetadata!.Should().NotContainKey("scope_id");
        callerScopeResolver.CapturedMetadata![LLMRequestMetadataKeys.NyxIdAccessToken]
            .Should().Be("runtime-user-token-1");
        callerScopeResolver.CapturedMetadata[LLMRequestMetadataKeys.NyxIdOrgToken]
            .Should().Be("runtime-user-token-1");
        callerScopeResolver.CapturedMetadata[ChannelMetadataKeys.MessageId]
            .Should().Be("msg-runtime-token-agent-builder-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRequestLlmReply_WhenUnboundPrivateSenderSendsNormalMessage()
    {
        var broker = new InMemoryCapabilityBroker();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var interactiveDispatcher = Substitute.For<IInteractiveReplyDispatcher>();
        interactiveDispatcher.DispatchAsync(
                Arg.Any<ChannelId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MessageContent>(),
                Arg.Any<ComposeContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InteractiveReplyDispatchResult(
                Succeeded: true,
                MessageId: "reply-binding-card-1",
                PlatformMessageId: "platform-binding-card-1",
                Capability: ComposeCapability.Exact,
                FellBackToText: false,
                Detail: null)));
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            services,
            interactiveReplyDispatcher: interactiveDispatcher);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-unbound-private-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-binding-1",
                    CorrelationId = "corr-binding-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-binding-1",
                replyToken: "relay-token-binding-1",
                replyMessageId: "relay-msg-binding-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().BeNullOrEmpty();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.ReplyToken.Should().Be("relay-token-binding-1");
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderBindingId);
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        result.Outbound.Cards.Should().BeEmpty();
        result.Outbound.Actions.Should().BeEmpty();
        adapter.Replies.Should().BeEmpty();
        await interactiveDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(
            default!,
            default!,
            default!,
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task RunInboundAsync_ShouldAttachSenderBindingAndToken_WhenBoundSenderSendsNormalMessage()
    {
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(
            new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = "scope-1",
                ExternalUserId = "ou_user_1",
            },
            new BindingId { Value = "bnd-user-1" });

        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "hello",
                "msg-bound-private-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                transportExtras: new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdOrgToken);
        result.LlmReplyRequest.Metadata.Should().NotContainKey("scope_id");
        var toolContext = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest!.ToolContext);
        var llmControl = LLMControlContextMapper.FromPayload(result.LlmReplyRequest.LlmControl);
        toolContext.Caller.ScopeId.Should().BeNull();
        toolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        toolContext.SenderBinding.BindingId.Should().Be("bnd-user-1");
        llmControl.SenderNyxIdAccessToken.Should().Be("test-access-token-for-bnd-user-1");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRequestLlmReply_WhenUnboundGroupSenderSendsNormalMessage()
    {
        var broker = new InMemoryCapabilityBroker();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("hello", "msg-unbound-group-1", ConversationScope.Group, "oc_group_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderBindingId);
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.SenderNyxIdAccessToken);
        adapter.Replies.Should().BeEmpty();
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    [Fact]
    public async Task RunInboundAsync_ShouldGateUnknownSlashCommandToInit_WhenSenderUnbound()
    {
        var broker = new InMemoryCapabilityBroker();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/foobar", "msg-unknown", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().BeNull();
        result.Outbound.Text.Should().Contain("/oauth/authorize");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("/oauth/authorize");
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    [Fact]
    public async Task RunInboundAsync_ShouldGateInvoiceSlashCommandToInit_WhenSenderUnbound()
    {
        var broker = new InMemoryCapabilityBroker();
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/invoice alice", "msg-unbound-invoice", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().BeNull();
        result.Outbound.Text.Should().Contain("/oauth/authorize");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("/oauth/authorize");
    }

    // Refactor (issue1318/first-slice): Old: unbound sender still saw tool dispatch + unknown
    // slash silently consumed.
    // New: unbound sender disables tool dispatch; unknown slash gates to /init bootstrap;
    // non-slash text path unchanged (owner-LLM chat fallback).
    [Fact]
    public async Task RunInboundAsync_ShouldRouteUnknownSlashCommandToOrnnSkillDiscovery_WhenSenderBound()
    {
        var broker = new InMemoryCapabilityBroker();
        broker.SeedBinding(
            new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = "scope-1",
                ExternalUserId = "ou_user_1",
            },
            new BindingId { Value = "bnd-user-1" });
        var services = new ServiceCollection()
            .AddSingleton<IExternalIdentityBindingQueryPort>(broker)
            .AddSingleton<INyxIdCapabilityBroker>(broker)
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/foobar", "msg-bound-unknown", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Contain("use_skill");
        result.LlmReplyRequest.Activity.Content.Text.Should().Contain("foobar");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.RequireInitialOrnnSearch.Should().BeTrue();
        recovery.RequireOrnnSearchOnBlocker.Should().BeTrue();
        recovery.CommandName.Should().Be("foobar");
        recovery.PrimarySkillName.Should().Be("foobar");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldNotAttachOrnnRecovery_ForRegisteredSlashCommandWhenSlashSubsystemFallsThrough()
    {
        var services = new ServiceCollection()
            .AddSingleton<IChannelSlashCommandHandler>(new NullSlashCommandHandler("custom"))
            .AddSingleton<ChannelSlashCommandRegistry>()
            .BuildServiceProvider();
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter, services);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity("/custom arg", "msg-custom-slash", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.LlmReplyRequest.Should().NotBeNull();
        result.LlmReplyRequest!.Activity.Content.Text.Should().Be("/custom arg");
        var recovery = AgentToolExecutionContextMapper.FromPayload(result.LlmReplyRequest.ToolContext).SkillRecovery;
        recovery.Should().Be(AgentSkillRecoveryContext.Empty);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldReturnPermanentFailure_WhenActivityIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-missing-activity",
                RegistrationId = "reg-1",
                Outbound = new MessageContent { Text = "hello" },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("activity_required");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldReturnProvidedTransientFailure_WhenOutboundIsEmpty()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-empty-reply",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-empty-reply"),
                Outbound = new MessageContent(),
                ErrorCode = "llm_timeout",
                ErrorSummary = "model timed out",
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("llm_timeout");
        result.ErrorSummary.Should().Be("model timed out");
        result.FailureKind.Should().Be(FailureKind.TransientAdapterError);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldSendDirectAdapterReply_WhenNoRelayDeliveryExists()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = string.Empty,
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-direct-llm-1"),
                Outbound = new MessageContent { Text = "direct reply" },
            },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-direct-llm-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("direct reply");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldReturnAdapterNotFound_WhenRegistrationPlatformHasNoAdapter()
    {
        var registration = BuildRegistrationEntry("reg-discord");
        registration.Platform = "discord";
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("reg-discord", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-no-adapter",
                RegistrationId = "reg-discord",
                Activity = BuildInboundActivity("hello", "msg-no-adapter", botId: "reg-discord"),
                Outbound = new MessageContent { Text = "direct reply" },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("adapter_not_found");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldMapPermanentAdapterRejection()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter
        {
            ReplyDeliveryResult = new PlatformReplyDeliveryResult(
                false,
                "recipient blocked bot",
                PlatformReplyFailureKind.Permanent),
        };
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-permanent-reply",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-permanent-reply"),
                Outbound = new MessageContent { Text = "direct reply" },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("reply_rejected");
        result.ErrorSummary.Should().Be("recipient blocked bot");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldMapTransientAdapterRejection()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter
        {
            ReplyDeliveryResult = new PlatformReplyDeliveryResult(
                false,
                "platform throttled",
                PlatformReplyFailureKind.Transient),
        };
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-transient-reply",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-transient-reply"),
                Outbound = new MessageContent { Text = "direct reply" },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("reply_rejected");
        result.ErrorSummary.Should().Be("platform throttled");
        result.FailureKind.Should().Be(FailureKind.TransientAdapterError);
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldUseTextFallback_ForInteractiveDirectReply()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);
        var outbound = new MessageContent();
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-direct-interactive",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-direct-interactive"),
                Outbound = outbound,
            },
            CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("Confirm");
        result.Outbound.Text.Should().Contain("Confirm");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldSendRelayReply_WhenReadyEventArrives()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-relay-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
            });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = new MessageContent { Text = "relay reply" },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-1");
        result.OutboundDelivery?.ReplyMessageId.Should().Be("relay-msg-1");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-1\"");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"relay reply\"");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldClearTypingReaction_AfterSuccessfulRelayReply()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-swap-1"}""");
        // Expect 2 nyx calls fired by the post-reply clear: list Typing → delete bot's
        // Typing reaction. The list response carries one bot-owned reaction
        // ("operator_type":"app") and one user-owned ("operator_type":"user") that the
        // clear must leave alone.
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 2,
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-1","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}},{"reaction_id":"r-user-1","operator":{"operator_type":"user","operator_id":"u-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            nyxHandler: nyxHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-swap-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-swap-1",
                CorrelationId = "corr-relay-swap-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
                NyxPlatformMessageId = "om_swap_1",
            });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-swap-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = new MessageContent { Text = "relay reply" },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-swap-1", replyMessageId: "relay-msg-swap-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await nyxHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        nyxHandler.Requests.Should().HaveCount(2);
        // 1. List the Typing reactions on the inbound message id.
        nyxHandler.Requests[0].Method.Should().Be("GET");
        nyxHandler.Requests[0].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_swap_1/reactions?reaction_type=Typing&page_size=50");
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer user-token-1");
        // 2. Only the bot-owned reaction is deleted; the user-owned one is preserved.
        nyxHandler.Requests[1].Method.Should().Be("DELETE");
        nyxHandler.Requests[1].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_swap_1/reactions/r-bot-1");
    }

    [Fact]
    public async Task OnReplyDeliveredAsync_ShouldClearTypingReaction_WhenStreamingPathInvokesIt()
    {
        // The streaming completion path in ConversationGAgent finalizes the reply through
        // RunStreamChunkAsync edits and never calls RunLlmReplyAsync, so the clear inside
        // RunLlmReplyAsync would be skipped on the most common production path. The GAgent
        // calls OnReplyDeliveredAsync to plug that gap; this test pins the runner end of the
        // contract so a refactor that drops the implementation in favor of a no-op default
        // would fail loudly here.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 2,
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-stream","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-stream-swap-1",
            transportExtras: new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
                NyxPlatformMessageId = "om_stream_swap_1",
            });

        await ((IConversationTurnRunner)runner).OnReplyDeliveredAsync(activity, CancellationToken.None);
        await nyxHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        nyxHandler.Requests.Should().HaveCount(2);
        nyxHandler.Requests[0].Method.Should().Be("GET");
        nyxHandler.Requests[0].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_stream_swap_1/reactions?reaction_type=Typing&page_size=50");
        nyxHandler.Requests[1].Method.Should().Be("DELETE");
        nyxHandler.Requests[1].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_stream_swap_1/reactions/r-bot-stream");
    }

    [Fact]
    public async Task RunLlmReplyAsync_RelayPath_ShouldStillReplyAndSkipReactionClear_WhenRegistrationLookupThrows()
    {
        // Reviewer guard: the post-reply clear needs registration for NyxProviderSlug, but the
        // relay reply itself uses the reply token and never touches the registration store. A
        // transient registration-store exception must NOT abort the relay reply — it should
        // degrade the clear to a no-op for that turn while the user-visible reply still lands.
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChannelBotRegistrationEntry?>>(_ => throw new InvalidOperationException("registration store unavailable"));
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-relay-no-reg"}""");
        // If the clear were to fire, it'd hit nyxHandler. The assertion below confirms it does NOT.
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            nyxHandler: nyxHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-no-reg",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-no-reg",
                CorrelationId = "corr-relay-no-reg",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
                NyxPlatformMessageId = "om_no_reg_1",
            });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-no-reg",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = new MessageContent { Text = "relay reply still lands" },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-no-reg", replyMessageId: "relay-msg-no-reg"),
            CancellationToken.None);

        // Reply delivered through the relay despite the registration store throwing.
        result.Success.Should().BeTrue();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"relay reply still lands\"");
        // Registration is required for the clear, so when lookup throws on the relay path the clear
        // is degraded to a no-op for that turn (no list / delete calls).
        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldPaginate_WhenTypingReactionListSpansMultiplePages()
    {
        // Lark's `list message reactions` is paginated. If the bot's own Typing reaction lands on
        // a later page (chat with many users reacting Typing), the original single-page clear would
        // miss it and leave Typing on the message. The clear must walk pages until has_more=false.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-paginated"}""");
        // 3 nyx calls expected: list page 1 (user only, has_more=true) → list page 2 (bot,
        // has_more=false) → DELETE bot reaction. (No call between pages — the loop
        // re-issues GET with page_token.)
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 3,
            """{"code":0,"data":{"items":[{"reaction_id":"r-user-1","operator":{"operator_type":"user","operator_id":"u-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":true,"page_token":"page-2-token"}}""",
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-late","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            nyxHandler: nyxHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-paginated",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-paginated",
                CorrelationId = "corr-relay-paginated",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
                NyxPlatformMessageId = "om_paginated_1",
            });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-paginated",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = new MessageContent { Text = "paginated reply" },
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-paginated", replyMessageId: "relay-msg-paginated"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await nyxHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        nyxHandler.Requests.Should().HaveCount(3);
        // 1. List page 1 — no page_token query param.
        nyxHandler.Requests[0].Method.Should().Be("GET");
        nyxHandler.Requests[0].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_paginated_1/reactions?reaction_type=Typing&page_size=50");
        // 2. List page 2 — same URL with page_token from page 1's response.
        nyxHandler.Requests[1].Method.Should().Be("GET");
        nyxHandler.Requests[1].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_paginated_1/reactions?reaction_type=Typing&page_size=50&page_token=page-2-token");
        // 3. DELETE the bot-owned reaction discovered on page 2.
        nyxHandler.Requests[2].Method.Should().Be("DELETE");
        nyxHandler.Requests[2].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_paginated_1/reactions/r-bot-late");
    }

    [Fact]
    public async Task OnReplyDeliveredAsync_ShouldNoOp_WhenActivityIsNotLark()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        // Missing NyxPlatformMessageId — the clear helper should short-circuit and never call nyx.
        var activity = BuildInboundActivity("hello", "msg-no-platform-id");

        await ((IConversationTurnRunner)runner).OnReplyDeliveredAsync(activity, CancellationToken.None);

        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldAwaitTypingReactionBeforeClear_ForDirectAgentBuilderReply()
    {
        // Direct-reply paths can return faster than the typing POST takes to land
        // in Lark. Without this guard the GET-list step of the clear would fire before the typing
        // reaction is persisted, find nothing to delete, and then the typing reaction would land
        // orphaned. This test pins the ordering by blocking the typing POST until after the clear
        // would have run; assertion is that the clear waited (issued no GET) until typing was
        // released, then issued GET → DELETE.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        // First nyx call is the typing POST (blocked); next 2 are the clear (list / delete).
        var nyxHandler = new TypingReactionGateHandler(
            expectedTotalCallCount: 3,
            """{"code":0,"data":{"reaction_id":"r-bot-direct"}}""",
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-direct","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        // /delete-agent without confirm is a local DirectReply decision (no tool execution, no
        // external NyxID calls), so the only nyx traffic on this turn is the typing POST + the
        // two clear calls. That keeps the SequencedJsonHandler bodies aligned with the actual
        // call order.
        var activity = BuildInboundActivity(
            "/delete-agent agent-1",
            "msg-direct-typing-1",
            ConversationScope.DirectMessage,
            "oc_p2p_chat_1",
            transportExtras: new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
                NyxPlatformMessageId = "om_direct_1",
            });

        var inboundTask = runner.RunInboundAsync(activity, CancellationToken.None);

        // Wait for the runner to fire the typing POST and reach the clear's await — at that point
        // the clear is parked on the typing TaskCompletionSource and has not yet issued the GET.
        await nyxHandler.TypingPostStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await inboundTask;
        result.Success.Should().BeTrue();

        // The handler records each request only AFTER its SendAsync returns — typing is parked
        // before recording, so an empty Requests list here means the clear has not raced ahead
        // with the GET while typing was still in-flight. If the guard regressed, a GET would
        // already be recorded as Request[0] at this point.
        nyxHandler.Requests.Should().BeEmpty();

        nyxHandler.ReleaseTypingPost.TrySetResult();
        await nyxHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // After release: POST Typing landed first, then GET → DELETE in order.
        nyxHandler.Requests.Should().HaveCount(3);
        nyxHandler.Requests[0].Method.Should().Be("POST");
        nyxHandler.Requests[0].Body.Should().Contain("\"emoji_type\":\"Typing\"");
        nyxHandler.Requests[1].Method.Should().Be("GET");
        nyxHandler.Requests[1].Path.Should().Contain("reaction_type=Typing");
        nyxHandler.Requests[2].Method.Should().Be("DELETE");
        nyxHandler.Requests[2].Path.Should().Contain("/reactions/r-bot-direct");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldNotClearReaction_WhenReplyFails()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter
        {
            ReplyDeliveryResult = new PlatformReplyDeliveryResult(
                false,
                "recipient blocked bot",
                PlatformReplyFailureKind.Permanent),
        };
        // Any nyx call here would be the post-reply clear firing. Fail early on it so
        // the test still proves the clear was skipped — Requests.Should().BeEmpty() below
        // makes the assertion explicit.
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-failed-reply",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-failed-reply",
                    transportExtras: new TransportExtras
                    {
                        NyxPlatform = "lark",
                        NyxUserAccessToken = "user-token-1",
                        NyxPlatformMessageId = "om_fail_1",
                    }),
                Outbound = new MessageContent { Text = "direct reply" },
            },
            CancellationToken.None);

        result.Success.Should().BeFalse();
        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldDispatchInteractiveRelayReply_WhenOutboundContainsActions()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var interactiveDispatcher = Substitute.For<IInteractiveReplyDispatcher>();
        interactiveDispatcher.DispatchAsync(
                Arg.Any<ChannelId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MessageContent>(),
                Arg.Any<ComposeContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InteractiveReplyDispatchResult(
                Succeeded: true,
                MessageId: "reply-card-1",
                PlatformMessageId: "platform-card-1",
                Capability: ComposeCapability.Exact,
                FellBackToText: false,
                Detail: null)));
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            interactiveReplyDispatcher: interactiveDispatcher);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-card-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-relay-card-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
            });
        var outbound = new MessageContent
        {
            Text = "Choose one",
        };
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-card-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = outbound,
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-card-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-card-1");
        result.Outbound.Actions.Should().ContainSingle();
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
        await interactiveDispatcher.Received(1).DispatchAsync(
            Arg.Is<ChannelId>(channel => channel.Value == "lark"),
            "relay-msg-1",
            "relay-token-1",
            Arg.Is<MessageContent>(message => message.Text == "Choose one" && message.Actions.Count == 1),
            Arg.Any<ComposeContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldSendRelayTextFallback_WhenInteractiveDispatcherIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-fallback-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-fallback-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-fallback-1",
                CorrelationId = "corr-relay-fallback-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
            });
        var outbound = new MessageContent
        {
            Text = "Choose one",
        };
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-fallback-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = outbound,
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-fallback-1", replyMessageId: "relay-msg-fallback-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-fallback-1");
        result.Outbound.Text.Should().Be("Choose one\n• Confirm");
        result.Outbound.Actions.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-fallback-1\"");
        relayHandler.Requests[0].Body.Should().Contain("Choose one");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldExposeTextFallback_WhenInteractiveDispatcherFallsBackToText()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var interactiveDispatcher = Substitute.For<IInteractiveReplyDispatcher>();
        interactiveDispatcher.DispatchAsync(
                Arg.Any<ChannelId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MessageContent>(),
                Arg.Any<ComposeContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InteractiveReplyDispatchResult(
                Succeeded: true,
                MessageId: "reply-text-fallback-1",
                PlatformMessageId: "platform-text-fallback-1",
                Capability: ComposeCapability.Degraded,
                FellBackToText: true,
                Detail: null)));
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            interactiveReplyDispatcher: interactiveDispatcher);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-dispatcher-text-fallback-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-dispatcher-text-fallback-1",
                CorrelationId = "corr-relay-dispatcher-text-fallback-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
            });
        var outbound = new MessageContent
        {
            Text = "Choose one",
        };
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-dispatcher-text-fallback-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = outbound,
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-dispatcher-text-fallback-1", replyMessageId: "relay-msg-dispatcher-text-fallback-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("reply-text-fallback-1");
        result.Outbound.Text.Should().Be("Choose one\n• Confirm");
        result.Outbound.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldNotRetryAsText_WhenInteractiveDispatcherFails()
    {
        // Production regression: NyxID's `channel-relay/reply` is single-use — even when the
        // interactive payload returns a transport-level failure (e.g. NyxID 502), the relay
        // token is already consumed. The legacy "degrade to text" path in
        // TrySendInteractiveRelayReplyAsync re-sent the same token as plain text, which always
        // came back as `401 Reply token already used`, escalated as `relay_reply_rejected`, and
        // queued an inbound turn retry that re-consumed the (already gone) token forever — bot
        // looked silent on every subsequent DM after PR #409 introduced interactive cards.
        //
        // The single-use semantics demand exactly one attempt per inbound. When the dispatcher
        // reports failure, the runner must surface a failure result without making a second
        // call to the relay HTTP API.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var interactiveDispatcher = Substitute.For<IInteractiveReplyDispatcher>();
        interactiveDispatcher.DispatchAsync(
                Arg.Any<ChannelId>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MessageContent>(),
                Arg.Any<ComposeContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InteractiveReplyDispatchResult(
                Succeeded: false,
                MessageId: null,
                PlatformMessageId: null,
                Capability: ComposeCapability.Exact,
                FellBackToText: false,
                Detail: "nyx_status=502 body=error code: 502")));
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler,
            interactiveReplyDispatcher: interactiveDispatcher);
        var activity = BuildInboundActivity(
            "hello",
            "msg-relay-card-fail-1",
            ConversationScope.Group,
            "oc_group_chat_1",
            new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-relay-card-fail-1",
            },
            new TransportExtras
            {
                NyxPlatform = "lark",
                NyxUserAccessToken = "user-token-1",
            });
        var outbound = new MessageContent
        {
            Text = "Choose one",
        };
        outbound.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var result = await runner.RunLlmReplyAsync(
            new LlmReplyReadyEvent
            {
                CorrelationId = "corr-relay-card-fail-1",
                RegistrationId = "reg-1",
                SourceActorId = "llm-worker-1",
                Activity = activity,
                Outbound = outbound,
                TerminalState = LlmReplyTerminalState.Completed,
                ReadyAtUnixMs = 42,
            },
            RelayRuntimeContext("corr-relay-card-fail-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        // Distinct error code routed to PermanentFailure (vs transient `relay_reply_rejected`)
        // so `ConversationGAgent.HandleInboundTurnTransientFailureAsync` does NOT queue an
        // `InboundTurnRetryScheduledEvent` that would re-run the inbound turn with the same
        // already-consumed reply token. Without this routing, the in-turn retry fix would just
        // shift the 401 cascade from in-turn replay to grain-level replay.
        result.ErrorCode.Should().Be("relay_reply_token_consumed");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.ErrorSummary.Should().Contain("502");

        // Critical assertion: the runner MUST NOT make a second HTTP call to NyxID's
        // channel-relay endpoint. The previous (broken) "degrade to text" path issued one
        // additional POST that always failed with 401 and trashed the inbound turn's retry
        // budget. Verify the relay handler stays clean.
        relayHandler.Requests.Should().BeEmpty();
        await interactiveDispatcher.Received(1).DispatchAsync(
            Arg.Any<ChannelId>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<MessageContent>(),
            Arg.Any<ComposeContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunContinueAsync_DirectMessageWithoutPartition_ReturnsPermanentFailure()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                partition: null,
                "dm",
                "user-open-id"),
            Payload = new MessageContent { Text = "hello" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("conversation_not_found");
        result.ErrorSummary.Should().Contain("routing target");
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunContinueAsync_OnBehalfOfUser_ReturnsUnsupportedAuthContext()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-user-1",
            CorrelationId = "corr-user-1",
            Kind = PrincipalKind.OnBehalfOfUser,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Payload = new MessageContent { Text = "hello" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("unsupported_auth_context");
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunContinueAsync_ReturnsRegistrationNotFound_WhenBotRegistrationIsMissing()
    {
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync("missing-reg", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(null));
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-missing-reg-1",
            CorrelationId = "corr-missing-reg-1",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("missing-reg"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Payload = new MessageContent { Text = "hello" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("registration_not_found");
        await registrationQueryPort.Received(1).GetAsync("missing-reg", Arg.Any<CancellationToken>());
        adapter.Replies.Should().BeEmpty();
    }

    [Fact]
    public async Task RunContinueAsync_GroupWithoutPartition_UsesCanonicalFallback()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunContinueAsync(new ConversationContinueRequestedEvent
        {
            CommandId = "cmd-2",
            CorrelationId = "corr-2",
            Kind = PrincipalKind.Bot,
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                partition: null,
                "group",
                "oc_group_chat_1"),
            Payload = new MessageContent { Text = "hello group" },
            DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.ConversationId.Should().Be("oc_group_chat_1");
        adapter.Replies[0].Inbound.ChatType.Should().Be("group");
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldSendInitialRelayChunk_AndReturnPlatformMessageId()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler(
            """{"message_id":"relay-send-1","platform_message_id":"om_stream_1"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-stream-1",
                    ConversationScope.Group,
                    "oc_group_chat_1",
                    new OutboundDeliveryContext
                    {
                        ReplyMessageId = "relay-msg-stream-1",
                        CorrelationId = "corr-stream-1",
                    },
                    new TransportExtras
                    {
                        NyxPlatform = "feishu",
                    }),
                AccumulatedText = " streamed reply ",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: null,
            RelayRuntimeContext("corr-stream-1", replyToken: "relay-token-stream-1", replyMessageId: "relay-msg-stream-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.PlatformMessageId.Should().Be("om_stream_1");
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-stream-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-stream-1\"");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"streamed reply\"");
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldUpdateExistingRelayChunk()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"upstream_message_id":"om_stream_2"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-update-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-stream-update-1",
                    ConversationScope.Group,
                    "oc_group_chat_1",
                    new OutboundDeliveryContext
                    {
                        ReplyMessageId = "relay-msg-stream-update-1",
                        CorrelationId = "corr-stream-update-1",
                    },
                    new TransportExtras
                    {
                        NyxPlatform = "lark",
                    }),
                AccumulatedText = "updated stream",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: "om_stream_1",
            RelayRuntimeContext(
                "corr-stream-update-1",
                replyToken: "relay-token-stream-update-1",
                replyMessageId: "relay-msg-stream-update-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.PlatformMessageId.Should().Be("om_stream_2");
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply/update");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-stream-update-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"om_stream_1\"");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"updated stream\"");
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldFlagEditUnsupported_WhenRelayUpdateRejectsEdit()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler(
            """{"error":true,"status":501,"body":"edit_unsupported"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-edit-unsupported-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-stream-edit-unsupported-1",
                    ConversationScope.Group,
                    "oc_group_chat_1",
                    new OutboundDeliveryContext
                    {
                        ReplyMessageId = "relay-msg-stream-edit-unsupported-1",
                        CorrelationId = "corr-stream-edit-unsupported-1",
                    },
                    new TransportExtras
                    {
                        NyxPlatform = "lark",
                    }),
                AccumulatedText = "updated stream",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: "om_stream_1",
            RelayRuntimeContext(
                "corr-stream-edit-unsupported-1",
                replyToken: "relay-token-stream-edit-unsupported-1",
                replyMessageId: "relay-msg-stream-edit-unsupported-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_edit_unsupported");
        result.EditUnsupported.Should().BeTrue();
        result.FailureKind.Should().Be(FailureKind.PermanentAdapterError);
        result.HttpStatus.Should().Be(501);
        result.ErrorSummary.Should().Contain("edit_unsupported");
        relayHandler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldPropagateRelayUpdateFailureDiagnostics()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler(
            """{"error":true,"status":429,"body":"{\"error\":\"rate_limited\",\"error_code\":1005,\"retry_after_seconds\":4}"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-rate-limited-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-stream-rate-limited-1",
                    ConversationScope.Group,
                    "oc_group_chat_1",
                    new OutboundDeliveryContext
                    {
                        ReplyMessageId = "relay-msg-stream-rate-limited-1",
                        CorrelationId = "corr-stream-rate-limited-1",
                    },
                    new TransportExtras
                    {
                        NyxPlatform = "lark",
                    }),
                AccumulatedText = "updated stream",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: "om_stream_1",
            RelayRuntimeContext(
                "corr-stream-rate-limited-1",
                replyToken: "relay-token-stream-rate-limited-1",
                replyMessageId: "relay-msg-stream-rate-limited-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("relay_reply_update_rejected");
        result.FailureKind.Should().Be(FailureKind.TransientAdapterError);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(4));
        result.HttpStatus.Should().Be(429);
        result.RawErrorKey.Should().Be("rate_limited");
        result.RawErrorCode.Should().Be(1005);
        relayHandler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldRejectInvalidDeliveryAndReplyToken()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"unexpected"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var missingDelivery = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-missing-delivery-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity("hello", "msg-stream-missing-delivery-1"),
                AccumulatedText = "streamed reply",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: null,
            RelayRuntimeContext("corr-stream-missing-delivery-1"),
            CancellationToken.None);

        var expiredToken = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-expired-token-1",
                RegistrationId = "reg-1",
                Activity = BuildInboundActivity(
                    "hello",
                    "msg-stream-expired-token-1",
                    ConversationScope.Group,
                    "oc_group_chat_1",
                    new OutboundDeliveryContext
                    {
                        ReplyMessageId = "relay-msg-stream-expired-token-1",
                        CorrelationId = "corr-stream-expired-token-1",
                    },
                    new TransportExtras
                    {
                        NyxPlatform = "lark",
                    }),
                AccumulatedText = "streamed reply",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: null,
            new ConversationTurnRuntimeContext(new NyxRelayReplyTokenContext(
                "corr-stream-expired-token-1",
                "relay-token-expired",
                "relay-msg-stream-expired-token-1",
                DateTimeOffset.UtcNow.AddSeconds(-1))),
            CancellationToken.None);

        missingDelivery.Success.Should().BeFalse();
        missingDelivery.ErrorCode.Should().Be("invalid_delivery");
        expiredToken.Success.Should().BeFalse();
        expiredToken.ErrorCode.Should().Be("reply_token_missing_or_expired");
        relayHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunStreamChunkAsync_ShouldRejectWhenActivityIsMissing()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunStreamChunkAsync(
            new LlmReplyStreamChunkEvent
            {
                CorrelationId = "corr-stream-missing-activity-1",
                RegistrationId = "reg-1",
                AccumulatedText = "streamed reply",
                ChunkAtUnixMs = 42,
            },
            currentPlatformMessageId: null,
            RelayRuntimeContext("corr-stream-missing-activity-1"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("activity_required");
    }

    public static IEnumerable<object[]> WorkflowResumeDispatchErrors()
    {
        yield return
        [
            WorkflowRunControlStartError.InvalidActorId(" "),
            "invalid_actor_id",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.InvalidRunId("actor-1", " "),
            "invalid_run_id",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.ActorNotFound("actor-1", "run-1"),
            "actor_not_found",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.ActorNotWorkflowRun("actor-1", "run-1"),
            "actor_not_workflow_run",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.RunBindingMissing("actor-1", "run-1"),
            "run_binding_missing",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.RunBindingMismatch("actor-1", "run-requested", "run-bound"),
            "run_binding_mismatch",
            FailureKind.PermanentAdapterError,
        ];
        yield return
        [
            WorkflowRunControlStartError.InvalidSignalName("actor-1", "run-1", " "),
            "workflow_resume_dispatch_failed",
            FailureKind.TransientAdapterError,
        ];
    }

    private static ChannelConversationTurnRunner CreateRunner(
        IChannelBotRegistrationQueryPort registrationQueryPort,
        RecordingPlatformAdapter adapter,
        IServiceProvider? services = null,
        IChannelBotRegistrationQueryByNyxIdentityPort? registrationQueryByNyxIdentityPort = null,
        RecordingJsonHandler? relayHandler = null,
        HttpMessageHandler? nyxHandler = null,
        IInteractiveReplyDispatcher? interactiveReplyDispatcher = null)
    {
        services ??= BuildAgentBuilderToolServices();
        relayHandler ??= new RecordingJsonHandler("""{"message_id":"relay-reply"}""");
        nyxHandler ??= new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var relayClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://example.com" },
            new HttpClient(relayHandler)
            {
                BaseAddress = new Uri("https://example.com"),
            },
            NullLogger<NyxIdApiClient>.Instance);
        var relayOutboundPort = new NyxIdRelayOutboundPort(
            relayClient,
            NullLogger<NyxIdRelayOutboundPort>.Instance,
            [new RelayStubComposer("lark")]);

        return new ChannelConversationTurnRunner(
            services,
            registrationQueryPort,
            registrationQueryByNyxIdentityPort,
            [adapter],
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://example.com" },
                new HttpClient(nyxHandler)
                {
                    BaseAddress = new Uri("https://example.com"),
                }),
            relayOutboundPort,
            interactiveReplyDispatcher,
            NullLogger<ChannelConversationTurnRunner>.Instance,
            // Production wires IOwnerLlmConfigSource via DI (ActivatorUtilities fills the
            // optional ctor param). Tests build their own ServiceProvider; pull the registered
            // source out of the test-supplied container so the existing tests that AddSingleton
            // a stub still resolve correctly without re-introducing a per-execution GetService.
            ownerLlmConfigSource: services.GetService<IOwnerLlmConfigSource>(),
            identityBindingQueryPort: services.GetService<IExternalIdentityBindingQueryPort>(),
            slashCommandRegistry: services.GetService<ChannelSlashCommandRegistry>(),
            capabilityBroker: services.GetService<INyxIdCapabilityBroker>(),
            userLlmSelectionService: services.GetService<IUserLlmSelectionService>(),
            userLlmOptionsService: services.GetService<IUserLlmOptionsService>(),
            userLlmOptionsRenderer: services.GetService<IUserLlmOptionsRenderer<MessageContent>>(),
            userConfigQueryPort: services.GetService<IUserConfigQueryPort>(),
            replyService: services.GetService<ChannelPlatformReplyService>(),
            workflowResumeService: services.GetService<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(),
            workflowDraftRunAdmission: services.GetService<ChannelWorkflowDraftRunAdmission>());
    }

    private static IServiceProvider BuildAgentBuilderToolServices(IScopeWorkflowQueryPort? workflowQueryPort = null)
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForChannel(
                "nyx-user-1",
                "lark",
                "scope-1",
                "ou_user_1")));

        var services = new ServiceCollection()
            .AddSingleton(queryPort)
            .AddSingleton(Substitute.For<ISkillRunnerExecutionQueryPort>())
            .AddSingleton(Substitute.For<ISkillRunnerCommandPort>())
            .AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>())
            .AddSingleton<ICallerScopeResolver>(callerScopeResolver)
            .AddSingleton<INyxIdApiClientFactory>(new TestNyxIdApiClientFactory())
            .AddSingleton<IChannelSlashCommandHandler, ChannelWorkflowDraftRunSlashCommandHandler>()
            .AddSingleton<ChannelSlashCommandRegistry>()
            .AddSingleton<ChannelWorkflowDraftRunIntentParser>()
            .AddSingleton(sp => new ChannelWorkflowDraftRunAdmission(
                sp.GetRequiredService<ChannelWorkflowDraftRunIntentParser>(),
                sp.GetService<IScopeWorkflowQueryPort>()));
        if (workflowQueryPort is not null)
            services.AddSingleton(workflowQueryPort);

        return services.BuildServiceProvider();
    }

    private static ConversationTurnRuntimeContext RelayRuntimeContext(
        string correlationId,
        string replyToken = "relay-token-1",
        string replyMessageId = "relay-msg-1",
        string? nyxUserAccessToken = null) =>
        new(new NyxRelayReplyTokenContext(
            correlationId,
            replyToken,
            replyMessageId,
            DateTimeOffset.UtcNow.AddMinutes(5),
            nyxUserAccessToken),
            nyxUserAccessToken);

    private static ChatActivity BuildInboundActivity(
        string text,
        string messageId,
        ConversationScope scope = ConversationScope.Group,
        string? partition = "oc_group_chat_1",
        OutboundDeliveryContext? outboundDelivery = null,
        TransportExtras? transportExtras = null,
        string botId = "reg-1")
    {
        return new ChatActivity
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From(botId),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From(botId),
                scope,
                partition,
                scope switch
                {
                    ConversationScope.Group => "group",
                    ConversationScope.Channel => "channel",
                    _ => "dm",
                },
                scope switch
                {
                    ConversationScope.Group => "oc_group_chat_1",
                    ConversationScope.Channel => "oc_channel_1",
                    _ => "ou_user_1",
                }),
            From = new ParticipantRef
            {
                CanonicalId = "ou_user_1",
                DisplayName = "User One",
            },
            Content = new MessageContent
            {
                Text = text,
            },
            OutboundDelivery = outboundDelivery?.Clone(),
            TransportExtras = transportExtras?.Clone(),
        };
    }

    private static ChatActivity BuildCardActionActivity(string eventId, params (string Key, string Value)[] fields)
    {
        var cardAction = new CardActionSubmission
        {
            SourceMessageId = "om-card-1",
        };

        foreach (var (key, value) in fields)
            cardAction.FormFields[key] = value;

        return new ChatActivity
        {
            Id = eventId,
            Type = ActivityType.CardAction,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                partition: "oc_chat_1",
                "dm",
                "ou_user_1"),
            From = new ParticipantRef
            {
                CanonicalId = "ou_user_1",
                DisplayName = "User One",
            },
            Content = new MessageContent
            {
                CardAction = cardAction,
            },
        };
    }

    private static IChannelBotRegistrationQueryPort BuildRegistrationQueryPort()
    {
        var registration = BuildRegistrationEntry();
        var queryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        queryPort.GetAsync(registration.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(registration));
        return queryPort;
    }

    private static ChannelBotRegistrationEntry BuildRegistrationEntry(string id = "reg-1") =>
        new()
        {
            Id = id,
            Platform = "lark",
            NyxProviderSlug = "api-lark-bot",
            ScopeId = "scope-1",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static ScopeWorkflowSummary BuildWorkflowSummary(
        string scopeId,
        string workflowId) =>
        new(
            scopeId,
            workflowId,
            $"Display {workflowId}",
            $"service-key-{workflowId}",
            workflowId,
            $"actor-{workflowId}",
            "rev-active",
            "deployment-1",
            "active",
            DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

    private sealed class StubScopeWorkflowQueryPort(ScopeWorkflowSummary? workflow) : IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>(workflow is null ? [] : [workflow]);

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal)
                ? workflow
                : null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult(workflow is not null &&
                            string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) &&
                            string.Equals(workflow.ActorId, actorId, StringComparison.Ordinal)
                ? workflow
                : null);
    }

    private sealed class RecordingPlatformAdapter : IPlatformAdapter
    {
        public string Platform => "lark";

        public List<(string ReplyText, InboundMessage Inbound)> Replies { get; } = [];
        public PlatformReplyDeliveryResult ReplyDeliveryResult { get; init; } = new(true, "ok");

        public Task<IResult?> TryHandleVerificationAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<IResult?>(null);

        public Task<InboundMessage?> ParseInboundAsync(HttpContext http, ChannelBotRegistrationEntry registration) =>
            Task.FromResult<InboundMessage?>(null);

        public Task<PlatformReplyDeliveryResult> SendReplyAsync(
            string replyText,
            InboundMessage inbound,
            ChannelBotRegistrationEntry registration,
            NyxIdApiClient nyxClient,
            CancellationToken ct)
        {
            Replies.Add((replyText, inbound));
            return Task.FromResult(ReplyDeliveryResult);
        }
    }

    private sealed class RelayStubComposer(string platform) : IMessageComposer<RelayStubPayload>
    {
        public ChannelId Channel { get; } = ChannelId.From(platform);

        public RelayStubPayload Compose(MessageContent intent, ComposeContext context) =>
            new(intent.Text ?? string.Empty);

        object IMessageComposer.Compose(MessageContent intent, ComposeContext context) => Compose(intent, context);

        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => ComposeCapability.Exact;
    }

    private sealed record RelayStubPayload(string PlainText) : IPlainTextComposedMessage;

    private sealed class NullSlashCommandHandler(string name) : IChannelSlashCommandHandler
    {
        public string Name { get; } = name;
        public bool RequiresBinding => false;
        public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct) =>
            Task.FromResult<MessageContent?>(null);
    }

    private sealed class RecordingWorkflowResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public required CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> Result { get; init; }

        public List<WorkflowResumeCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class CapturingCallerScopeResolver : ICallerScopeResolver
    {
        public IReadOnlyDictionary<string, string>? CapturedMetadata { get; private set; }

        public Task<OwnerScope?> TryResolveAsync(CancellationToken ct = default)
        {
            var current = AgentToolRequestContext.Current;
            CapturedMetadata = current is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = current.Credentials.NyxIdAccessToken ?? string.Empty,
                    [LLMRequestMetadataKeys.NyxIdOrgToken] = current.Credentials.NyxIdOrgToken ?? string.Empty,
                    [ChannelMetadataKeys.MessageId] = current.Channel.MessageId ?? string.Empty,
                };
            return Task.FromResult<OwnerScope?>(OwnerScope.ForChannel(
                "nyx-user-1",
                "lark",
                "scope-1",
                "ou_user_1"));
        }
    }

    private sealed class TestNyxIdApiClientFactory : INyxIdApiClientFactory
    {
        private readonly NyxIdApiClient _client;

        public TestNyxIdApiClientFactory(NyxIdApiClient? client = null)
        {
            _client = client ?? new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://example.com" },
                new HttpClient(new RecordingJsonHandler("""{"ok":true}"""))
                {
                    BaseAddress = new Uri("https://example.com"),
                });
        }

        public NyxIdApiClient CreateClient() => _client;
    }

    private sealed class StubUserLlmOptionsService(UserLlmOption option) : IUserLlmOptionsService
    {
        public Task<UserLlmOptionsView> GetOptionsAsync(UserLlmOptionsQuery query, CancellationToken ct) =>
            Task.FromResult(new UserLlmOptionsView(null, [option], null));
    }

    private sealed class RecordingUserLlmSelectionService : IUserLlmSelectionService
    {
        public string? SelectedServiceId { get; private set; }
        public string? SelectedModel { get; private set; }
        public string? PresetId { get; private set; }
        public bool ResetCalled { get; private set; }
        public UserLlmSelectionContext? Context { get; private set; }

        public Task SetByServiceAsync(
            UserLlmSelectionContext context,
            string serviceId,
            string? modelOverride,
            CancellationToken ct)
        {
            Context = context;
            SelectedServiceId = serviceId;
            SelectedModel = modelOverride;
            return Task.CompletedTask;
        }

        public Task SetModelOverrideAsync(
            UserLlmSelectionContext context,
            string model,
            CancellationToken ct)
        {
            Context = context;
            SelectedModel = model;
            return Task.CompletedTask;
        }

        public Task ApplyPresetAsync(
            UserLlmSelectionContext context,
            string presetId,
            CancellationToken ct)
        {
            Context = context;
            PresetId = presetId;
            return Task.CompletedTask;
        }

        public Task ResetAsync(UserLlmSelectionContext context, CancellationToken ct)
        {
            Context = context;
            ResetCalled = true;
            return Task.CompletedTask;
        }
    }

    private class RecordingJsonHandler(string body) : HttpMessageHandler
    {
        public List<(string Path, string Method, string? Authorization, string Body)> Requests { get; } = [];

        protected virtual string ResolveBody() => body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Method.Method,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResolveBody(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class BlockingJsonHandler(string body) : RecordingJsonHandler(body)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // Parks the FIRST request (the typing POST that fires from RunInboundAsync) on a
    // TaskCompletionSource until the test releases it. Used by the race test to confirm that
    // the post-reply clear awaits the typing POST before issuing the GET-list — without the
    // guard, the clear GET would run while typing is still parked here.
    private sealed class TypingReactionGateHandler : RecordingJsonHandler
    {
        private readonly Queue<string> _bodies;
        private readonly int _expectedTotalCallCount;
        private int _callCount;

        public TypingReactionGateHandler(int expectedTotalCallCount, params string[] bodies)
            : base(bodies.Length > 0 ? bodies[0] : """{"code":0,"data":{}}""")
        {
            _expectedTotalCallCount = expectedTotalCallCount;
            _bodies = new Queue<string>(bodies);
        }

        public TaskCompletionSource TypingPostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTypingPost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string ResolveBody() => _bodies.Count > 0 ? _bodies.Dequeue() : """{"code":0,"data":{}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _callCount);
            if (index == 1)
            {
                TypingPostStarted.TrySetResult();
                await ReleaseTypingPost.Task.WaitAsync(cancellationToken);
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (Requests.Count >= _expectedTotalCallCount)
                Completed.TrySetResult();
            return response;
        }
    }

    // Returns a different body for each successive call; signals Completed once expectedCallCount
    // requests have been served. Extends RecordingJsonHandler which captures Path, Method,
    // Authorization, and Body — the Method field lets reaction tests assert GET/DELETE ordering.
    private sealed class SequencedJsonHandler : RecordingJsonHandler
    {
        private readonly Queue<string> _bodies;
        private readonly int _expectedCallCount;

        public SequencedJsonHandler(int expectedCallCount, params string[] bodies)
            : base(bodies.Length > 0 ? bodies[0] : """{"code":0,"data":{}}""")
        {
            _expectedCallCount = expectedCallCount;
            _bodies = new Queue<string>(bodies);
        }

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override string ResolveBody() => _bodies.Count > 0 ? _bodies.Dequeue() : """{"code":0,"data":{}}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Requests.Count >= _expectedCallCount)
                Completed.TrySetResult();
            return response;
        }
    }

    private sealed class StubOwnerLlmConfigSource : IOwnerLlmConfigSource
    {
        private readonly OwnerLlmConfig _config;
        private readonly bool _throwOnGet;

        public StubOwnerLlmConfigSource(OwnerLlmConfig? config = null, bool throwOnGet = false)
        {
            _config = config ?? OwnerLlmConfig.Empty;
            _throwOnGet = throwOnGet;
        }

        public List<string> Calls { get; } = [];

        public Task<OwnerLlmConfig> GetForScopeAsync(string scopeId, CancellationToken ct = default)
        {
            Calls.Add(scopeId);
            if (_throwOnGet)
                throw new InvalidOperationException("simulated owner-config lookup failure");
            return Task.FromResult(_config);
        }
    }
}
