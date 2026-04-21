using System.Reflection;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public class ChannelUserGAgentContinuationTests
{
    [Fact]
    public async Task HandleChatEnd_ShouldSendReply_AndCompletePendingSession()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();

        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "hello ",
            }));
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "world",
            }));
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = "hello world",
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("hello world");
        agent.State.PendingSessions.Should().BeEmpty();
        scheduler.Canceled.Should().ContainSingle(x => x.CallbackId == $"chat-timeout-{request.SessionId}");
        streams.GetRecordingStream("channel-lark-reg-1-ou_123").RemovedTargets
            .Should().ContainSingle("channel-user-lark-reg-1-ou_123");
    }

    [Fact]
    public async Task HandleChatTimeout_SelfEnvelope_ShouldSendFallbackReply()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-user-lark-reg-1-ou_123",
            TopologyAudience.Self,
            new ChannelChatTimeoutEvent
            {
                SessionId = request.SessionId,
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("Sorry, it's taking too long to respond. Please try again.");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChatTimeout_ShouldSendPartialReply_WhenBufferedContentExists()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "partial reply",
            }));

        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-user-lark-reg-1-ou_123",
            TopologyAudience.Self,
            new ChannelChatTimeoutEvent
            {
                SessionId = request.SessionId,
            }));

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("partial reply");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_ShouldRecoverPendingSession_AndRedispatchChatRequest()
    {
        const string actorId = "channel-user-lark-reg-1-ou_123";

        var eventStore = new InMemoryEventStore();
        var runtime1 = new RecordingActorRuntime();
        var streams1 = new RecordingStreamProvider();
        var scheduler1 = new RecordingCallbackScheduler();
        var adapter1 = new RecordingPlatformAdapter("lark");
        using (var services1 = BuildServices(runtime1, streams1, scheduler1, adapter1, eventStore))
        {
            var agent1 = CreateAgent(services1, actorId);
            await agent1.ActivateAsync();
            await agent1.HandleInbound(BuildInboundEvent());
            runtime1.ChatRequests.Should().ContainSingle();
        }

        var runtime2 = new RecordingActorRuntime();
        var streams2 = new RecordingStreamProvider();
        var scheduler2 = new RecordingCallbackScheduler();
        var adapter2 = new RecordingPlatformAdapter("lark");
        using var services2 = BuildServices(runtime2, streams2, scheduler2, adapter2, eventStore);
        var agent2 = CreateAgent(services2, actorId);

        await agent2.ActivateAsync();

        runtime2.ChatRequests.Should().ContainSingle();
        var recovered = runtime2.ChatRequests[0];
        recovered.Prompt.Should().Be("hello from lark");
        recovered.ScopeId.Should().Be("scope-1");
        scheduler2.TimeoutRequests.Should().ContainSingle(x => x.CallbackId == $"chat-timeout-{recovered.SessionId}");
        streams2.GetRecordingStream("channel-lark-reg-1-ou_123").Bindings
            .Should().ContainSingle(x => x.TargetStreamId == actorId);
    }

    [Fact]
    public async Task ActivateAsync_ShouldRecoverExpiredPendingSession_BySchedulingImmediateTimeoutWithoutRedispatch()
    {
        const string actorId = "channel-user-lark-reg-1-ou_123";

        var eventStore = new InMemoryEventStore();
        await SeedPendingSessionAsync(
            eventStore,
            actorId,
            new ChannelPendingChatSession
            {
                SessionId = "expired-session",
                ChatActorId = "channel-lark-reg-1-ou_123",
                OrgToken = "org-token",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                SenderId = "ou_123",
                SenderName = "Alice",
                MessageId = "om_msg_1",
                ChatType = "p2p",
                RegistrationId = "reg-1",
                NyxProviderSlug = "api-lark-bot",
                RegistrationScopeId = "scope-1",
                Prompt = "hello from lark",
                TimeoutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1)),
            });

        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, eventStore);
        var agent = CreateAgent(services, actorId);

        await agent.ActivateAsync();

        runtime.ChatRequests.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle(x => x.CallbackId == "chat-timeout-expired-session");
        streams.GetRecordingStream("channel-lark-reg-1-ou_123").Bindings.Should().BeEmpty();

        await agent.HandleEventAsync(scheduler.TimeoutRequests[0].TriggerEnvelope);

        adapter.Replies.Should().ContainSingle();
        adapter.Replies[0].ReplyText.Should().Be("Sorry, it's taking too long to respond. Please try again.");
        agent.State.PendingSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleInbound_ShouldDedupProcessedMessageId()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().ContainSingle();
        agent.State.PendingSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleInbound_ShouldResumePendingSession_WhenPreviousDispatchFailed()
    {
        var runtime = new RecordingActorRuntime
        {
            FailChatRequestsRemaining = 1,
        };
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();

        await agent.ActivateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.HandleInbound(inbound));

        runtime.ChatRequests.Should().BeEmpty();
        agent.State.PendingSessions.Should().ContainSingle();
        agent.State.ProcessedMessageIds.Should().BeEmpty();

        await agent.HandleInbound(inbound);
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().ContainSingle();
        agent.State.PendingSessions.Should().ContainSingle();
        agent.State.ProcessedMessageIds.Should().Contain("om_msg_1");
    }

    [Fact]
    public async Task HandleChatEnd_ShouldKeepPendingSession_AndExposeDiagnostics_WhenReplyDeliveryIsRejected()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark")
        {
            FailRepliesRemaining = 1,
            FailureDetail = "lark_code=230002 msg=Bot not in chat",
        };
        var diagnostics = new InMemoryChannelRuntimeDiagnostics();
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore(), diagnostics);
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageContentEvent
            {
                SessionId = request.SessionId,
                Delta = "hello world",
            }));
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = "hello world",
            }));

        adapter.Replies.Should().ContainSingle();
        agent.State.PendingSessions.Should().ContainSingle();
        diagnostics.GetRecent().Select(entry => entry.Stage).Should().Contain("Reply:error");

        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-user-lark-reg-1-ou_123",
            TopologyAudience.Self,
            new ChannelChatTimeoutEvent
            {
                SessionId = request.SessionId,
            }));

        adapter.Replies.Should().HaveCount(2);
        adapter.Replies[1].ReplyText.Should().Be("hello world");
        agent.State.PendingSessions.Should().BeEmpty();
        diagnostics.GetRecent().Select(entry => entry.Stage).Should().Contain("Reply:done");
    }

    [Fact]
    public async Task HandleChatEnd_ShouldCompletePendingSession_WhenReplyDeliveryPermanentFailure()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark")
        {
            FailRepliesRemaining = 1,
            FailureDetail = "lark_code=230002 msg=Bot not in chat",
            FailureKind = PlatformReplyFailureKind.Permanent,
        };
        var diagnostics = new InMemoryChannelRuntimeDiagnostics();
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore(), diagnostics);
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        var request = runtime.SingleChatRequest();
        await agent.HandleEventAsync(BuildTopologyEnvelope(
            "channel-lark-reg-1-ou_123",
            TopologyAudience.Parent,
            new TextMessageEndEvent
            {
                SessionId = request.SessionId,
                Content = "hello world",
            }));

        adapter.Replies.Should().ContainSingle();
        agent.State.PendingSessions.Should().BeEmpty();
        diagnostics.GetRecent().Select(entry => entry.Stage).Should().Contain("Reply:permanent");
    }

    [Fact]
    public async Task HandleInbound_DailyReportIntent_ShouldSendInteractiveBuilderCard()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/daily-report";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Create Daily Report Agent");

        agent.State.PendingSessions.Should().BeEmpty();
        agent.State.ProcessedMessageIds.Should().Contain("om_msg_1");
    }

    [Fact]
    public async Task HandleInbound_SocialMediaIntent_ShouldSendInteractiveBuilderCard()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/social-media";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Create Social Media Agent");

        agent.State.PendingSessions.Should().BeEmpty();
        agent.State.ProcessedMessageIds.Should().Contain("om_msg_1");
    }

    [Fact]
    public async Task HandleInbound_CreateDailyReportCardAction_ShouldExecuteAgentBuilder()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = callInfo.ArgAt<string>(0),
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(Arg.Any<string>()).Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/proxy/services", """
            [
              {"id":"svc-github","slug":"api-github"},
              {"id":"svc-lark","slug":"api-lark-bot"}
            ]
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-1","full_key":"full-key-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
                serviceCollection.AddSingleton(nyxClient);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"create_daily_report"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_card_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "create_daily_report" },
                { "github_username", "alice" },
                { "repositories", "aevatarAI/aevatar" },
                { "schedule_time", "09:00" },
                { "schedule_timezone", "UTC" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Daily report agent created: skill-runner-");
        adapter.Replies[0].ReplyText.Should().Contain("View Agents");
        agent.State.PendingSessions.Should().BeEmpty();
        agent.State.ProcessedMessageIds.Should().Contain("evt_card_1");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                e.Payload.Unpack<InitializeSkillRunnerCommand>().TemplateName == "daily_report" &&
                e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ConversationId == "oc_chat_1"),
            Arg.Any<CancellationToken>());

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "create_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_CreateDailyReportCardAction_ShouldReturnGitHubCredentialsCard_WhenUserCredentialsMissing()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """{"tokens":[]}""");
        handler.Add(HttpMethod.Get, "/api/v1/catalog/api-github", """
            {
              "slug":"api-github",
              "provider_config_id":"provider-github",
              "provider_type":"oauth2",
              "credential_mode":"user",
              "documentation_url":"https://docs.github.com/en/apps/oauth-apps"
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/credentials", """
            {
              "provider_config_id":"provider-github",
              "has_credentials":false
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
                serviceCollection.AddSingleton(nyxClient);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"create_daily_report"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_card_oauth_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "create_daily_report" },
                { "github_username", "alice" },
                { "repositories", "aevatarAI/aevatar" },
                { "schedule_time", "09:00" },
                { "schedule_timezone", "UTC" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("GitHub Credentials Required");
        adapter.Replies[0].ReplyText.Should().Contain("OAuth Docs");
        adapter.Replies[0].ReplyText.Should().Contain("https://docs.github.com/en/apps/oauth-apps");
        agent.State.ProcessedMessageIds.Should().Contain("evt_card_oauth_1");

        await actorRuntime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_CreateSocialMediaCardAction_ShouldExecuteAgentBuilder()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = callInfo.ArgAt<string>(0),
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync(Arg.Any<string>()).Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<WorkflowAgentGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(workflowAgentActor));

        var workflowCommandPort = Substitute.For<IScopeWorkflowCommandPort>();
        workflowCommandPort.UpsertAsync(Arg.Any<ScopeWorkflowUpsertRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScopeWorkflowUpsertResult(
                new ScopeWorkflowSummary(
                    "scope-1",
                    "social-media-workflow-agent-1",
                    "Social Media Approval workflow-agent-1",
                    "service-key",
                    "social_media_workflow_agent_1",
                    "workflow-actor-1",
                    "rev-1",
                    "deploy-1",
                    "active",
                    DateTimeOffset.UtcNow),
                "rev-1",
                "workflow-actor-prefix",
                "workflow-actor-1")));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/proxy/services", """
            [
              {"id":"svc-lark","slug":"api-lark-bot"}
            ]
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-2","full_key":"full-key-2"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
                serviceCollection.AddSingleton(workflowCommandPort);
                serviceCollection.AddSingleton(nyxClient);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"create_social_media"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_card_social_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "create_social_media" },
                { "topic", "Launch update for the new workflow feature" },
                { "audience", "Developers" },
                { "style", "Confident and concise" },
                { "schedule_time", "09:00" },
                { "schedule_timezone", "UTC" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Social media agent created: workflow-agent-");
        adapter.Replies[0].ReplyText.Should().Contain("View Agents");
        agent.State.PendingSessions.Should().BeEmpty();
        agent.State.ProcessedMessageIds.Should().Contain("evt_card_social_1");

        await workflowAgentActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(InitializeWorkflowAgentCommand.Descriptor) &&
                e.Payload.Unpack<InitializeWorkflowAgentCommand>().WorkflowActorId == "workflow-actor-1" &&
                e.Payload.Unpack<InitializeWorkflowAgentCommand>().ConversationId == "oc_chat_1"),
            Arg.Any<CancellationToken>());

        await workflowAgentActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "create_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_ListAgentsIntent_ShouldSendInteractiveAgentListCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRegistryEntry>>(
            [
                new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = "running",
                    NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
                    OwnerNyxUserId = "user-1",
                },
            ]));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            runtime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
                serviceCollection.AddSingleton(nyxClient);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/agents";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Create Daily Report");
        adapter.Replies[0].ReplyText.Should().Contain("Create Social Media");
        adapter.Replies[0].ReplyText.Should().Contain("Refresh List");
        adapter.Replies[0].ReplyText.Should().Contain("View Templates");
        adapter.Replies[0].ReplyText.Should().Contain("/enable-agent");
        adapter.Replies[0].ReplyText.Should().Contain("/disable-agent");
        adapter.Replies[0].ReplyText.Should().Contain("agent_status");
        adapter.Replies[0].ReplyText.Should().Contain("run_agent");
        adapter.Replies[0].ReplyText.Should().Contain("confirm_delete_agent");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Current Agents");
    }

    [Fact]
    public async Task HandleInbound_ListTemplatesIntent_ShouldSendInteractiveTemplateCard()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/templates";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("daily_report");
        adapter.Replies[0].ReplyText.Should().Contain("social_media");
        adapter.Replies[0].ReplyText.Should().Contain("Create Daily Report");
        adapter.Replies[0].ReplyText.Should().Contain("Create Social Media");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Available Templates");
    }

    [Fact]
    public async Task HandleInbound_AgentStatusTextCommand_ShouldSendStatusCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = "running",
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
                NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
            }));

        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            runtime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/agent-status skill-runner-1";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Agent Status");
    }

    [Fact]
    public async Task HandleInbound_RunAgentTextCommand_ShouldExecuteTriggerAndSendResultCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/run-agent skill-runner-1";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Run Triggered");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "run_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_DisableAgentTextCommand_ShouldExecuteDisableAndSendStatusCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/disable-agent skill-runner-1";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Agent disabled");
        adapter.Replies[0].ReplyText.Should().Contain("Enable Agent");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Agent Status");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "disable_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_DeleteAgentTextCommand_ShouldSendDeleteConfirmationCard()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = BuildInboundEvent();
        inbound.Text = "/delete-agent skill-runner-1";

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Confirm Delete");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Delete Agent");
    }

    [Fact]
    public async Task HandleInbound_OpenDailyReportFormCardAction_ShouldSendBuilderCard()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"open_daily_report_form"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_open_form_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "open_daily_report_form" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        runtime.ChatRequests.Should().BeEmpty();
        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Create Daily Report Agent");
    }

    [Fact]
    public async Task HandleInbound_AgentStatusCardAction_ShouldSendStatusCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = "running",
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
                LastRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero)),
                NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
                ErrorCount = 0,
                ConversationId = "oc_chat_1",
            }));

        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            runtime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"agent_status"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_status_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "agent_status" },
                { "agent_id", "skill-runner-1" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Delete Agent");
        adapter.Replies[0].ReplyText.Should().Contain("Refresh Status");
        adapter.Replies[0].ReplyText.Should().Contain("Disable Agent");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Agent Status");
    }

    [Fact]
    public async Task HandleInbound_DeleteAgentCardAction_ShouldExecuteDeleteAndSendResultCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    ApiKeyId = "key-1",
                    OwnerNyxUserId = "user-1",
                }),
                Task.FromResult<AgentRegistryEntry?>(null));
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRegistryEntry>>(Array.Empty<AgentRegistryEntry>()));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");
        var registryActor = Substitute.For<IActor>();
        registryActor.Id.Returns(UserAgentCatalogGAgent.WellKnownId);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));
        actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(registryActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
                serviceCollection.AddSingleton(nyxClient);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"delete_agent"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_delete_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "delete_agent" },
                { "agent_id", "skill-runner-1" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Deleted agent");
        adapter.Replies[0].ReplyText.Should().Contain("No agents found yet");
        adapter.Replies[0].ReplyText.Should().Contain("View Templates");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Current Agents");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "delete_agent"),
            Arg.Any<CancellationToken>());

        await registryActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(AgentRegistryTombstoneCommand.Descriptor) &&
                e.Payload.Unpack<AgentRegistryTombstoneCommand>().AgentId == "skill-runner-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_RunAgentCardAction_ShouldExecuteTriggerAndSendResultCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"run_agent"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_run_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "run_agent" },
                { "agent_id", "skill-runner-1" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Refresh Status");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Run Triggered");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "run_agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_RunWorkflowAgentCardAction_ShouldPropagateRevisionFeedback()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
            {
                AgentId = "workflow-agent-1",
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(workflowAgentActor));

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"run_agent"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_run_workflow_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "run_agent" },
                { "agent_id", "workflow-agent-1" },
                { "revision_feedback", "Need stronger hook" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Refresh Status");

        await workflowAgentActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "run_agent" &&
                e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().RevisionFeedback == "Need stronger hook"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleInbound_EnableAgentCardAction_ShouldExecuteEnableAndSendStatusCard()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<AgentRegistryEntry?>(new AgentRegistryEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                    NextRunAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
                }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(
            actorRuntime,
            streams,
            scheduler,
            adapter,
            new InMemoryEventStore(),
            configure: serviceCollection =>
            {
                serviceCollection.AddSingleton(queryPort);
            });

        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");
        var inbound = new ChannelInboundEvent
        {
            Text = """{"action":"enable_agent"}""",
            SenderId = "ou_123",
            SenderName = "Alice",
            ConversationId = "oc_chat_1",
            MessageId = "evt_enable_1",
            ChatType = "card_action",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "session-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
            Extra =
            {
                { "agent_builder_action", "enable_agent" },
                { "agent_id", "skill-runner-1" },
            },
        };

        await agent.ActivateAsync();
        await agent.HandleInbound(inbound);

        adapter.Replies.Should().ContainSingle();
        LarkPlatformAdapter.IsInteractiveCardPayload(adapter.Replies[0].ReplyText).Should().BeTrue();
        adapter.Replies[0].ReplyText.Should().Contain("Agent enabled");
        adapter.Replies[0].ReplyText.Should().Contain("Disable Agent");

        using var card = JsonDocument.Parse(adapter.Replies[0].ReplyText);
        card.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString()
            .Should().Be("Agent Status");

        await skillRunnerActor.Received(1).HandleEventAsync(
            Arg.Is<EventEnvelope>(e =>
                e.Payload != null &&
                e.Payload.Is(EnableSkillRunnerCommand.Descriptor) &&
                e.Payload.Unpack<EnableSkillRunnerCommand>().Reason == "enable_agent"),
            Arg.Any<CancellationToken>());
    }

    // ─── Durable Dedup Tests ───

    [Fact]
    public async Task HandleInbound_PersistsMessageIdAfterSuccessfulDispatch()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_123");

        await agent.ActivateAsync();
        await agent.HandleInbound(BuildInboundEvent());

        agent.State.ProcessedMessageIds.Should().Contain("om_msg_1");
    }

    [Fact]
    public async Task DurableDedup_SurvivesDeactivationAndReactivation()
    {
        const string actorId = "channel-user-lark-reg-1-ou_dedup";
        var eventStore = new InMemoryEventStore();
        var inbound = new ChannelInboundEvent
        {
            Text = "first message",
            SenderId = "ou_dedup",
            SenderName = "Bob",
            ConversationId = "oc_dedup_chat",
            MessageId = "om_dedup_msg_1",
            ChatType = "p2p",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "org-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
        };

        // Activation 1: process a message
        var runtime1 = new RecordingActorRuntime();
        var streams1 = new RecordingStreamProvider();
        var scheduler1 = new RecordingCallbackScheduler();
        var adapter1 = new RecordingPlatformAdapter("lark");
        using (var services1 = BuildServices(runtime1, streams1, scheduler1, adapter1, eventStore))
        {
            var agent1 = CreateAgent(services1, actorId);
            await agent1.ActivateAsync();
            await agent1.HandleInbound(inbound);

            runtime1.ChatRequests.Should().ContainSingle();
            agent1.State.ProcessedMessageIds.Should().Contain("om_dedup_msg_1");
        }

        // Activation 2: same messageId should be rejected (dedup from persisted state)
        var runtime2 = new RecordingActorRuntime();
        var streams2 = new RecordingStreamProvider();
        var scheduler2 = new RecordingCallbackScheduler();
        var adapter2 = new RecordingPlatformAdapter("lark");
        using var services2 = BuildServices(runtime2, streams2, scheduler2, adapter2, eventStore);
        var agent2 = CreateAgent(services2, actorId);
        await agent2.ActivateAsync();

        runtime2.ChatRequests.Should().ContainSingle();
        agent2.State.ProcessedMessageIds.Should().Contain("om_dedup_msg_1");
        await agent2.HandleInbound(inbound);
        runtime2.ChatRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task DurableDedup_DoesNotPersistBeforeSuccessfulDispatchAcrossReactivation()
    {
        const string actorId = "channel-user-lark-reg-1-ou_retry";
        var eventStore = new InMemoryEventStore();
        var inbound = new ChannelInboundEvent
        {
            Text = "retry me",
            SenderId = "ou_retry",
            SenderName = "Retry",
            ConversationId = "oc_retry_chat",
            MessageId = "om_retry_msg_1",
            ChatType = "p2p",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "org-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
        };

        var runtime1 = new RecordingActorRuntime
        {
            FailChatRequestsRemaining = 1,
        };
        var streams1 = new RecordingStreamProvider();
        var scheduler1 = new RecordingCallbackScheduler();
        var adapter1 = new RecordingPlatformAdapter("lark");
        using (var services1 = BuildServices(runtime1, streams1, scheduler1, adapter1, eventStore))
        {
            var agent1 = CreateAgent(services1, actorId);
            await agent1.ActivateAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => agent1.HandleInbound(inbound));

            agent1.State.PendingSessions.Should().ContainSingle();
            agent1.State.ProcessedMessageIds.Should().BeEmpty();
        }

        var runtime2 = new RecordingActorRuntime();
        var streams2 = new RecordingStreamProvider();
        var scheduler2 = new RecordingCallbackScheduler();
        var adapter2 = new RecordingPlatformAdapter("lark");
        using var services2 = BuildServices(runtime2, streams2, scheduler2, adapter2, eventStore);
        var agent2 = CreateAgent(services2, actorId);

        await agent2.ActivateAsync();

        runtime2.ChatRequests.Should().ContainSingle();
        agent2.State.ProcessedMessageIds.Should().Contain("om_retry_msg_1");

        await agent2.HandleInbound(inbound);

        runtime2.ChatRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task PersistProcessedMessageId_BoundsProcessedMessageIdsTo200()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_bound");

        await agent.ActivateAsync();

        // Send 210 messages with unique messageIds
        for (var i = 0; i < 210; i++)
        {
            var inbound = new ChannelInboundEvent
            {
                Text = $"message {i}",
                SenderId = "ou_bound",
                SenderName = "Test",
                ConversationId = "oc_bound_chat",
                MessageId = $"om_bound_{i:D4}",
                ChatType = "p2p",
                Platform = "lark",
                RegistrationId = "reg-1",
                RegistrationToken = "org-token",
                RegistrationScopeId = "scope-1",
                NyxProviderSlug = "api-lark-bot",
            };

            // Complete the previous session before sending a new message
            if (i > 0)
            {
                var prevRequest = runtime.ChatRequests[i - 1];
                await agent.HandleEventAsync(BuildTopologyEnvelope(
                    "channel-lark-reg-1-ou_bound",
                    TopologyAudience.Parent,
                    new TextMessageEndEvent
                    {
                        SessionId = prevRequest.SessionId,
                        Content = $"reply {i - 1}",
                    }));
            }

            await agent.HandleInbound(inbound);
        }

        // ProcessedMessageIds should be bounded to 200
        agent.State.ProcessedMessageIds.Count.Should().BeLessThanOrEqualTo(200);

        // The oldest messageIds should have been evicted
        agent.State.ProcessedMessageIds.Should().NotContain("om_bound_0000");
        // The newest should still be present
        agent.State.ProcessedMessageIds.Should().Contain("om_bound_0209");
    }

    [Fact]
    public async Task HandleInbound_WithNullMessageId_SkipsDedup()
    {
        var runtime = new RecordingActorRuntime();
        var streams = new RecordingStreamProvider();
        var scheduler = new RecordingCallbackScheduler();
        var adapter = new RecordingPlatformAdapter("lark");
        using var services = BuildServices(runtime, streams, scheduler, adapter, new InMemoryEventStore());
        var agent = CreateAgent(services, "channel-user-lark-reg-1-ou_null");

        await agent.ActivateAsync();

        // Send message without messageId — should still be processed
        var inbound = new ChannelInboundEvent
        {
            Text = "no message id",
            SenderId = "ou_null",
            SenderName = "NoId",
            ConversationId = "oc_null_chat",
            MessageId = "", // empty
            ChatType = "p2p",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationToken = "org-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-lark-bot",
        };

        await agent.HandleInbound(inbound);
        runtime.ChatRequests.Should().ContainSingle();
    }

    private static ChannelInboundEvent BuildInboundEvent() => new()
    {
        Text = "hello from lark",
        SenderId = "ou_123",
        SenderName = "Alice",
        ConversationId = "oc_chat_1",
        MessageId = "om_msg_1",
        ChatType = "p2p",
        Platform = "lark",
        RegistrationId = "reg-1",
        RegistrationToken = "org-token",
        RegistrationScopeId = "scope-1",
        NyxProviderSlug = "api-lark-bot",
    };

    private static EventEnvelope BuildTopologyEnvelope(
        string publisherActorId,
        TopologyAudience audience,
        IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, audience),
        };

    private static async Task SeedPendingSessionAsync(
        InMemoryEventStore eventStore,
        string actorId,
        ChannelPendingChatSession session)
    {
        await eventStore.AppendAsync(actorId,
        [
            new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = 1,
                EventType = ChannelUserTrackedEvent.Descriptor.FullName,
                EventData = Any.Pack(new ChannelUserTrackedEvent
                {
                    Platform = session.Platform,
                    PlatformUserId = session.SenderId,
                    DisplayName = session.SenderName,
                }),
                AgentId = actorId,
            },
            new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = 2,
                EventType = ChannelChatRequestedEvent.Descriptor.FullName,
                EventData = Any.Pack(new ChannelChatRequestedEvent
                {
                    Session = session,
                }),
                AgentId = actorId,
            },
        ], 0);
    }

    private static ServiceProvider BuildServices(
        IActorRuntime runtime,
        RecordingStreamProvider streams,
        RecordingCallbackScheduler scheduler,
        RecordingPlatformAdapter adapter,
        IEventStore eventStore,
        IChannelRuntimeDiagnostics? diagnostics = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .AddSingleton<IActorRuntime>(runtime)
            .AddSingleton<IStreamProvider>(streams)
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddSingleton<IMemoryCache, MemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<IChannelRuntimeDiagnostics>(diagnostics ?? new InMemoryChannelRuntimeDiagnostics())
            .AddSingleton(new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://example.com" }))
            .AddSingleton<IPlatformAdapter>(adapter);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ChannelUserGAgent CreateAgent(IServiceProvider services, string actorId)
    {
        var agent = new ChannelUserGAgent
        {
            Services = services,
            Logger = NullLogger.Instance,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChannelUserState>>(),
        };

        typeof(GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        return agent;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, RecordingActor> _actors = new(StringComparer.Ordinal);

        public List<ChatRequestEvent> ChatRequests { get; } = [];
        public int FailChatRequestsRemaining { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new RecordingActor(actorId, this);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ChatRequestEvent SingleChatRequest() => ChatRequests.Should().ContainSingle().Subject;

        private sealed class RecordingActor(string id, RecordingActorRuntime owner) : IActor
        {
            public string Id { get; } = id;
            public IAgent Agent { get; } = null!;

            public Task ActivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task DeactivateAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true)
                {
                    if (owner.FailChatRequestsRemaining > 0)
                    {
                        owner.FailChatRequestsRemaining--;
                        throw new InvalidOperationException("simulated chat dispatch failure");
                    }

                    owner.ChatRequests.Add(envelope.Payload.Unpack<ChatRequestEvent>());
                }
                return Task.CompletedTask;
            }

            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
                Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId) => GetRecordingStream(actorId);

        public RecordingStream GetRecordingStream(string actorId)
        {
            if (_streams.TryGetValue(actorId, out var stream))
                return stream;

            stream = new RecordingStream(actorId);
            _streams[actorId] = stream;
            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;
        public List<StreamForwardingBinding> Bindings { get; } = [];
        public List<string> RemovedTargets { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new AsyncDisposableStub());
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Bindings.Add(binding);
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemovedTargets.Add(targetStreamId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(Bindings);
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];
        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPlatformAdapter(string platform) : IPlatformAdapter
    {
        public string Platform { get; } = platform;
        public List<ReplyRecord> Replies { get; } = [];
        public int FailRepliesRemaining { get; set; }
        public string FailureDetail { get; set; } = "reply_rejected";
        public PlatformReplyFailureKind FailureKind { get; set; } = PlatformReplyFailureKind.None;

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
            ct.ThrowIfCancellationRequested();
            Replies.Add(new ReplyRecord(replyText, inbound, registration));
            if (FailRepliesRemaining > 0)
            {
                FailRepliesRemaining--;
                return Task.FromResult(new PlatformReplyDeliveryResult(false, FailureDetail, FailureKind));
            }

            return Task.FromResult(new PlatformReplyDeliveryResult(true, "ok"));
        }
    }

    private sealed record ReplyRecord(
        string ReplyText,
        InboundMessage Inbound,
        ChannelBotRegistrationEntry Registration);

    private sealed class AsyncDisposableStub : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            var latest = stream.Count == 0 ? 0 : stream[^1].Version;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

        public void Add(HttpMethod method, string path, string body)
        {
            _responses[BuildKey(method, path)] = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = BuildKey(request.Method, request.RequestUri?.PathAndQuery ?? string.Empty);
            if (!_responses.TryGetValue(key, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":"not_found"}"""),
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }

        private static string BuildKey(HttpMethod method, string path) => $"{method.Method} {path}";
    }
}
