using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
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
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelConversationTurnRunnerTests
{
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
        result.LlmReplyRequest.TargetActorId.Should().Be("channel-conversation:lark:group:oc_group_chat_1");
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
    public async Task RunInboundAsync_ShouldApplyOwnerUserConfigOverridesToLlmMetadata_WhenSourceRegistered()
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
        result.LlmReplyRequest!.Metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("gpt-5.5");
        result.LlmReplyRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/chrono-llm");
        result.LlmReplyRequest.Metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride].Should().Be("12");
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
        result.LlmReplyRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
        result.LlmReplyRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/api/v1/proxy/s/chrono-llm");
        result.LlmReplyRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride);
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

        var result = await runner.RunInboundAsync(
            BuildCardActionActivity(
                "evt-card-1",
                ("actor_id", "actor-1"),
                ("run_id", "run-1"),
                ("step_id", "approval-1"),
                ("approved", "false"),
                ("user_input", "Need stronger hook")),
            CancellationToken.None);

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
        activity.Content.CardAction.Arguments["agent_builder_action"] = "open_daily_report_form";

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:evt-card-builder-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.ChatType.Should().Be("card_action");
        adapter.Replies[0].Inbound.Extra.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("open_daily_report_form");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldRouteAgentBuilderCardAction_WhenActionIdCarriesAgentBuilderAction()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var activity = BuildCardActionActivity("evt-card-builder-action-id-1");
        activity.Content.CardAction.ActionId = "open_daily_report_form";

        var result = await runner.RunInboundAsync(activity, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:evt-card-builder-action-id-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].Inbound.Extra.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("open_daily_report_form");
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
        var activity = BuildCardActionActivity(
            "evt-llm-select-1",
            (TextUserLlmOptionsRenderer.LlmActionArgument, TextUserLlmOptionsRenderer.SelectServiceAction),
            (TextUserLlmOptionsRenderer.ServiceIdArgument, "svc-openai"));

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
    public async Task RunInboundAsync_ShouldRouteSlashCommand_WhenRegistrationHasNoRelayApiKey()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily alice",
                "msg-slash-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-slash-1");
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("Create daily report agent failed");
        adapter.Replies[0].ReplyText.Should().Contain("No NyxID access token available");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSendRelayReply_ForDailySlashCommand_WhenRelayDeliveryIsPresent()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-daily"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily alice",
                "msg-daily-relay-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-daily-1",
                    CorrelationId = "corr-daily-relay-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-daily-relay-1",
                "relay-token-daily-1",
                "relay-msg-daily-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SentActivityId.Should().Be("direct-reply:msg-daily-relay-1");
        result.OutboundDelivery?.ReplyMessageId.Should().Be("relay-msg-daily-1");
        result.OutboundDelivery?.CorrelationId.Should().Be("corr-daily-relay-1");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-daily-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-daily-1\"");
        relayHandler.Requests[0].Body.Should().Contain("\"text\":\"Create daily report agent failed");
        relayHandler.Requests[0].Body.Should().Contain("No NyxID access token available");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldSendRelayRestriction_ForDailySlashCommandInGroup()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-group"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                "/daily alice",
                "msg-daily-group-1",
                ConversationScope.Group,
                "oc_group_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-group-1",
                    CorrelationId = "corr-daily-group-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-daily-group-1",
                "relay-token-group-1",
                "relay-msg-group-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Path.Should().Be("/api/v1/channel-relay/reply");
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-group-1");
        relayHandler.Requests[0].Body.Should().Contain("\"message_id\":\"relay-msg-group-1\"");
        relayHandler.Requests[0].Body.Should().Contain("private chat");
        relayHandler.Requests[0].Body.Should().Contain("/daily");
    }

    [Theory]
    [InlineData("/daily_report")]
    [InlineData("/foobar")]
    public async Task RunInboundAsync_ShouldSendRelayUsage_ForUnknownSlashCommand(string command)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"relay-reply-unknown"}""");
        var runner = CreateRunner(
            registrationQueryPort,
            adapter,
            relayHandler: relayHandler);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(
                command,
                "msg-unknown-relay-1",
                ConversationScope.DirectMessage,
                "oc_p2p_chat_1",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-unknown-1",
                    CorrelationId = "corr-unknown-relay-1",
                },
                new TransportExtras
                {
                    NyxPlatform = "lark",
                }),
            RelayRuntimeContext(
                "corr-unknown-relay-1",
                "relay-token-unknown-1",
                "relay-msg-unknown-1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().ContainSingle();
        relayHandler.Requests[0].Authorization.Should().Be("Bearer relay-token-unknown-1");
        relayHandler.Requests[0].Body.Should().Contain($"Unknown command: {command}");
        relayHandler.Requests[0].Body.Should().Contain("Supported commands:");
    }

    [Fact]
    public async Task RunInboundAsync_ShouldFailRelayDailySlashCommand_WhenReplyTokenIsMissing()
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
                "msg-daily-missing-token-1",
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
            ConversationTurnRuntimeContext.Empty,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("reply_token_missing_or_expired");
        adapter.Replies.Should().BeEmpty();
        relayHandler.Requests.Should().BeEmpty();
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
    public async Task RunInboundAsync_ShouldSendBindingCard_WhenUnboundPrivateSenderSendsNormalMessage()
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
        result.SentActivityId.Should().Be("reply-binding-card-1");
        result.LlmReplyRequest.Should().BeNull();
        result.Outbound.Cards.Should().ContainSingle(card => card.Title == "完成 NyxID 绑定");
        result.Outbound.Actions.Should().ContainSingle(action =>
            action.Kind == ActionElementKind.Link &&
            action.IsPrimary &&
            action.Value.Contains("test-nyxid.local/oauth/authorize"));
        adapter.Replies.Should().BeEmpty();
        await interactiveDispatcher.Received(1).DispatchAsync(
            Arg.Is<ChannelId>(channel => channel.Value == "lark"),
            "relay-msg-binding-1",
            "relay-token-binding-1",
            Arg.Is<MessageContent>(message =>
                message.Cards.Count == 1 &&
                message.Actions.Count == 1 &&
                message.Actions[0].Value.Contains("test-nyxid.local/oauth/authorize")),
            Arg.Any<ComposeContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunInboundAsync_ShouldPromptPrivateChatWithoutSlashCommand_WhenUnboundGroupSender()
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
        result.LlmReplyRequest.Should().BeNull();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain("请与 bot 私聊任意消息以获取 NyxID 绑定卡片。");
        adapter.Replies[0].ReplyText.Should().NotContain("/init");
    }

    [Theory]
    [InlineData("/daily_report")]
    [InlineData("/foobar")]
    [InlineData("/")]
    public async Task RunInboundAsync_ShouldShortCircuitUnknownSlashCommand_WithUsage(string command)
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var runner = CreateRunner(registrationQueryPort, adapter);

        var result = await runner.RunInboundAsync(
            BuildInboundActivity(command, "msg-unknown", ConversationScope.DirectMessage, "oc_p2p_chat_1"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Contain($"Unknown command: {command}");
        adapter.Replies[0].ReplyText.Should().Contain("Supported commands:");
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
    public async Task RunLlmReplyAsync_ShouldSwapTypingReactionToDone_AfterSuccessfulRelayReply()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-swap-1"}""");
        // Expect 3 nyx calls fired by the post-reply swap: list Typing → delete bot's
        // Typing reaction → add DONE. The list response carries one bot-owned reaction
        // ("operator_type":"app") and one user-owned ("operator_type":"user") that the
        // swap must leave alone.
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 3,
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-1","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}},{"reaction_id":"r-user-1","operator":{"operator_type":"user","operator_id":"u-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""",
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

        nyxHandler.Requests.Should().HaveCount(3);
        // 1. List the Typing reactions on the inbound message id.
        nyxHandler.Requests[0].Method.Should().Be("GET");
        nyxHandler.Requests[0].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_swap_1/reactions?reaction_type=Typing&page_size=50");
        nyxHandler.Requests[0].Authorization.Should().Be("Bearer user-token-1");
        // 2. Only the bot-owned reaction is deleted; the user-owned one is preserved.
        nyxHandler.Requests[1].Method.Should().Be("DELETE");
        nyxHandler.Requests[1].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_swap_1/reactions/r-bot-1");
        // 3. DONE reaction is added on the same message.
        nyxHandler.Requests[2].Method.Should().Be("POST");
        nyxHandler.Requests[2].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_swap_1/reactions");
        nyxHandler.Requests[2].Body.Should().Contain("\"emoji_type\":\"DONE\"");
    }

    [Fact]
    public async Task OnReplyDeliveredAsync_ShouldRunSwap_WhenStreamingPathInvokesIt()
    {
        // The streaming completion path in ConversationGAgent finalizes the reply through
        // RunStreamChunkAsync edits and never calls RunLlmReplyAsync, so the swap inside
        // RunLlmReplyAsync would be skipped on the most common production path. The GAgent
        // calls OnReplyDeliveredAsync to plug that gap; this test pins the runner end of the
        // contract so a refactor that drops the implementation in favor of a no-op default
        // would fail loudly here.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 3,
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-stream","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""",
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

        nyxHandler.Requests.Should().HaveCount(3);
        nyxHandler.Requests[0].Method.Should().Be("GET");
        nyxHandler.Requests[0].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_stream_swap_1/reactions?reaction_type=Typing&page_size=50");
        nyxHandler.Requests[1].Method.Should().Be("DELETE");
        nyxHandler.Requests[1].Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_stream_swap_1/reactions/r-bot-stream");
        nyxHandler.Requests[2].Method.Should().Be("POST");
        nyxHandler.Requests[2].Body.Should().Contain("\"emoji_type\":\"DONE\"");
    }

    [Fact]
    public async Task RunLlmReplyAsync_RelayPath_ShouldStillReplyAndSkipSwap_WhenRegistrationLookupThrows()
    {
        // Reviewer guard: the post-reply swap needs registration for NyxProviderSlug, but the
        // relay reply itself uses the reply token and never touches the registration store. A
        // transient registration-store exception must NOT abort the relay reply — it should
        // degrade the swap to a no-op for that turn while the user-visible reply still lands.
        var registrationQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        registrationQueryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChannelBotRegistrationEntry?>>(_ => throw new InvalidOperationException("registration store unavailable"));
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-relay-no-reg"}""");
        // If the swap were to fire, it'd hit nyxHandler. The assertion below confirms it does NOT.
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
        // Registration is required for the swap, so when lookup throws on the relay path the swap
        // is degraded to a no-op for that turn (no list / delete / DONE calls).
        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldPaginate_WhenTypingReactionListSpansMultiplePages()
    {
        // Lark's `list message reactions` is paginated. If the bot's own Typing reaction lands on
        // a later page (chat with many users reacting Typing), the original single-page swap would
        // miss it and leave Typing alongside DONE. The swap must walk pages until has_more=false.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var relayHandler = new RecordingJsonHandler("""{"message_id":"reply-paginated"}""");
        // 5 nyx calls expected: list page 1 (user only, has_more=true) → list page 2 (bot,
        // has_more=false) → DELETE bot reaction → POST DONE. (No call between pages — the loop
        // re-issues GET with page_token.)
        var nyxHandler = new SequencedJsonHandler(
            expectedCallCount: 4,
            """{"code":0,"data":{"items":[{"reaction_id":"r-user-1","operator":{"operator_type":"user","operator_id":"u-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":true,"page_token":"page-2-token"}}""",
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-late","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""",
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

        nyxHandler.Requests.Should().HaveCount(4);
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
        // 4. POST DONE.
        nyxHandler.Requests[3].Method.Should().Be("POST");
        nyxHandler.Requests[3].Body.Should().Contain("\"emoji_type\":\"DONE\"");
    }

    [Fact]
    public async Task OnReplyDeliveredAsync_ShouldNoOp_WhenActivityIsNotLark()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        var nyxHandler = new RecordingJsonHandler("""{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        // Missing NyxPlatformMessageId — the swap helper should short-circuit and never call nyx.
        var activity = BuildInboundActivity("hello", "msg-no-platform-id");

        await ((IConversationTurnRunner)runner).OnReplyDeliveredAsync(activity, CancellationToken.None);

        nyxHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunInboundAsync_ShouldAwaitTypingReactionBeforeSwap_ForDirectAgentBuilderReply()
    {
        // Direct-reply paths (e.g. /daily) can return faster than the typing POST takes to land
        // in Lark. Without this guard the GET-list step of the swap would fire before the typing
        // reaction is persisted, find nothing to delete, add DONE, and then the typing reaction
        // would land orphaned alongside DONE. This test pins the ordering by blocking the typing
        // POST until after the swap would have run; assertion is that the swap waited (issued no
        // GET) until typing was released, then issued GET → DELETE → POST DONE.
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter();
        // First nyx call is the typing POST (blocked); next 3 are the swap (list / delete / DONE).
        var nyxHandler = new TypingReactionGateHandler(
            expectedTotalCallCount: 4,
            """{"code":0,"data":{"reaction_id":"r-bot-direct"}}""",
            """{"code":0,"data":{"items":[{"reaction_id":"r-bot-direct","operator":{"operator_type":"app","operator_id":"bot-1"},"reaction_type":{"emoji_type":"Typing"}}],"has_more":false}}""",
            """{"code":0,"data":{}}""",
            """{"code":0,"data":{}}""");
        var runner = CreateRunner(registrationQueryPort, adapter, nyxHandler: nyxHandler);

        // /foobar is an unknown slash command — NyxRelayAgentBuilderFlow returns a DirectReply
        // decision (no tool execution, no external NyxID calls), so the only nyx traffic on this
        // turn is the typing POST + the three swap calls. That keeps the SequencedJsonHandler
        // bodies aligned with the actual call order.
        var activity = BuildInboundActivity(
            "/foobar",
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

        // Wait for the runner to fire the typing POST and reach the swap's await — at that point
        // the swap is parked on the typing TaskCompletionSource and has not yet issued the GET.
        await nyxHandler.TypingPostStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await inboundTask;
        result.Success.Should().BeTrue();

        // The handler records each request only AFTER its SendAsync returns — typing is parked
        // before recording, so an empty Requests list here means the swap has not raced ahead
        // with the GET while typing was still in-flight. If the guard regressed, a GET would
        // already be recorded as Request[0] at this point.
        nyxHandler.Requests.Should().BeEmpty();

        nyxHandler.ReleaseTypingPost.TrySetResult();
        await nyxHandler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // After release: POST Typing landed first, then GET → DELETE → POST DONE in order.
        nyxHandler.Requests.Should().HaveCount(4);
        nyxHandler.Requests[0].Method.Should().Be("POST");
        nyxHandler.Requests[0].Body.Should().Contain("\"emoji_type\":\"Typing\"");
        nyxHandler.Requests[1].Method.Should().Be("GET");
        nyxHandler.Requests[1].Path.Should().Contain("reaction_type=Typing");
        nyxHandler.Requests[2].Method.Should().Be("DELETE");
        nyxHandler.Requests[2].Path.Should().Contain("/reactions/r-bot-direct");
        nyxHandler.Requests[3].Method.Should().Be("POST");
        nyxHandler.Requests[3].Body.Should().Contain("\"emoji_type\":\"DONE\"");
    }

    [Fact]
    public async Task RunLlmReplyAsync_ShouldNotSwapReaction_WhenReplyFails()
    {
        var registrationQueryPort = BuildRegistrationQueryPort();
        var adapter = new RecordingPlatformAdapter
        {
            ReplyDeliveryResult = new PlatformReplyDeliveryResult(
                false,
                "recipient blocked bot",
                PlatformReplyFailureKind.Permanent),
        };
        // Any nyx call here would be the post-reply swap firing. Fail early on it so
        // the test still proves the swap was skipped — Requests.Should().BeEmpty() below
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
        result.Outbound.Text.Should().Be("Choose one");
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
        result.Outbound.Text.Should().Be("Choose one");
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
        result.ErrorSummary.Should().Contain("edit_unsupported");
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
        services ??= new ServiceCollection().BuildServiceProvider();
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
            workflowResumeService: services.GetService<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>());
    }

    private static ConversationTurnRuntimeContext RelayRuntimeContext(
        string correlationId,
        string replyToken = "relay-token-1",
        string replyMessageId = "relay-msg-1") =>
        new(new NyxRelayReplyTokenContext(
            correlationId,
            replyToken,
            replyMessageId,
            DateTimeOffset.UtcNow.AddMinutes(5)));

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
    // the post-reply swap awaits the typing POST before issuing the GET-list — without the
    // guard, the swap GET would run while typing is still parked here.
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
    // Authorization, and Body — the Method field lets swap tests assert GET/DELETE/POST ordering.
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
