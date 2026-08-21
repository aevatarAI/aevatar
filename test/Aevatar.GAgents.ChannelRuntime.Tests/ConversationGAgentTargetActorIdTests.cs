using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationGAgentTargetActorIdTests
{
    [Fact]
    public async Task HandleLlmReplyReadyAsync_ShouldPinProfileAndForwardItToLaterRuns()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredReplyTurnRunner();
        var dispatcher = new RecordingLlmReplyRunDispatcher();
        var agent = await CreateAgentAsync(actorId, runner, dispatcher);
        var profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-channel-alpha",
            ProfileVersion = "profile-v1",
            PublishedRevision = 1,
            AgentKind = "channel.reply",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "workspace.default",
            ActivationMode = AgentProfileActivationMode.Enforced,
        });

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-1"));
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = runner.Request.CorrelationId,
            RunId = runner.Request.RunId,
            RegistrationId = runner.Request.RegistrationId,
            SourceActorId = "channel-agent-run:agent-run-target-1",
            Activity = BuildInboundActivity("msg-target-1"),
            Outbound = new MessageContent { Text = "completed" },
            TerminalState = LlmReplyTerminalState.Completed,
            AgentProfile = profile.Clone(),
        });

        AgentProfileSnapshotCodec.ByteEquivalent(agent.State.AgentProfile, profile).Should().BeTrue();
        runner.Request.CorrelationId = "msg-target-2";
        runner.Request.RunId = "agent-run-target-2";
        runner.Request.Activity = BuildInboundActivity("msg-target-2");
        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-2"));

        dispatcher.Requests.Should().HaveCount(2);
        AgentProfileSnapshotCodec.ByteEquivalent(dispatcher.Requests[1].AgentProfile, profile).Should().BeTrue();
        AgentProfileSnapshotCodec.ByteEquivalent(
            agent.State.PendingLlmReplyRequests.Should().ContainSingle().Subject.AgentProfile,
            profile).Should().BeTrue();
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenProfileDiffersFromConversationPin_ShouldFailClosed()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredReplyTurnRunner();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher());
        var pinnedProfile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-channel-alpha",
            ProfileVersion = "profile-v1",
            PublishedRevision = 1,
            AgentKind = "channel.reply",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "workspace.default",
            ActivationMode = AgentProfileActivationMode.Enforced,
        });
        var changedProfile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-channel-alpha",
            ProfileVersion = "profile-v2",
            PublishedRevision = 2,
            AgentKind = "channel.reply",
            PolicyRevision = "policy-v2",
            RouteToolSetRef = "workspace.default",
            ActivationMode = AgentProfileActivationMode.Enforced,
        });

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-1"));
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = runner.Request.CorrelationId,
            RunId = runner.Request.RunId,
            RegistrationId = runner.Request.RegistrationId,
            Activity = BuildInboundActivity("msg-target-1"),
            Outbound = new MessageContent { Text = "first" },
            TerminalState = LlmReplyTerminalState.Completed,
            AgentProfile = pinnedProfile.Clone(),
        });

        runner.Request.CorrelationId = "msg-target-2";
        runner.Request.RunId = "agent-run-target-2";
        runner.Request.Activity = BuildInboundActivity("msg-target-2");
        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-2"));
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = runner.Request.CorrelationId,
            RunId = runner.Request.RunId,
            RegistrationId = runner.Request.RegistrationId,
            Activity = BuildInboundActivity("msg-target-2"),
            Outbound = new MessageContent { Text = "must not escape" },
            TerminalState = LlmReplyTerminalState.Completed,
            AgentProfile = changedProfile,
        });

        AgentProfileSnapshotCodec.ByteEquivalent(agent.State.AgentProfile, pinnedProfile).Should().BeTrue();
        runner.Replies.Should().HaveCount(2);
        var rejected = runner.Replies[1];
        rejected.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        rejected.ErrorCode.Should().Be("agent_profile_pin_mismatch");
        rejected.AgentProfile.Should().BeNull();
        rejected.AppendedHistory.Should().BeEmpty();
        rejected.Outbound.Text.Should().NotContain("must not escape");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_ShouldOwnerStampLlmReplyTargetActorIdBeforeDispatch()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredReplyTurnRunner();
        var dispatcher = new RecordingLlmReplyRunDispatcher();
        var agent = await CreateAgentAsync(actorId, runner, dispatcher);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-1"));

        runner.Request.TargetActorId.Should().BeEmpty();
        dispatcher.Requests.Should().ContainSingle();
        dispatcher.Requests[0].TargetActorId.Should().Be(actorId);
        agent.State.PendingLlmReplyRequests.Should().ContainSingle();
        agent.State.PendingLlmReplyRequests[0].TargetActorId.Should().Be(actorId);
    }

    [Theory]
    [InlineData(true, "callback-reply-message-1")]
    [InlineData(false, "original-reply-message-1")]
    public async Task HandleLlmReplyReadyAsync_ShouldSelectSourceActivityDeliveryContextOnlyWhenRequested(
        bool useSourceActivityDeliveryContext,
        string expectedReplyMessageId)
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredReplyTurnRunner();
        runner.Request.Activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "original-reply-message-1",
            CorrelationId = runner.Request.CorrelationId,
        };
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher());
        var inboundActivity = BuildInboundActivity("msg-target-1");
        inboundActivity.OutboundDelivery = runner.Request.Activity.OutboundDelivery.Clone();
        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            CorrelationId = runner.Request.CorrelationId,
            ReplyToken = "original-reply-token-1",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });
        var callbackActivity = BuildInboundActivity("callback-approval-1");
        callbackActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "callback-reply-message-1",
            CorrelationId = "callback-approval-1",
        };

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = runner.Request.CorrelationId,
            RunId = runner.Request.RunId,
            RegistrationId = runner.Request.RegistrationId,
            SourceActorId = "channel-agent-run:agent-run-target-1",
            Activity = callbackActivity,
            Outbound = new MessageContent { Text = "completed" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyToken = "callback-reply-token-1",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            UseSourceActivityDeliveryContext = useSourceActivityDeliveryContext,
        });

        var context = runner.ReplyRuntimeContexts.Should().ContainSingle().Subject;
        context.NyxRelayReplyToken.Should().NotBeNull();
        context.NyxRelayReplyToken!.ReplyToken.Should().Be("callback-reply-token-1");
        context.NyxRelayReplyToken.ReplyMessageId.Should().Be(expectedReplyMessageId);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WithWorkflowDeliveryDelegation_ShouldCompleteWithoutVisibleReply()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var runner = new DeferredReplyTurnRunner();
        var agent = await CreateAgentAsync(
            actorId,
            runner,
            new RecordingLlmReplyRunDispatcher(),
            eventStore: eventStore);
        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-target-1"));
        agent.State.PendingLlmReplyRequests.Should().ContainSingle();

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = runner.Request.CorrelationId,
            RunId = runner.Request.RunId,
            RegistrationId = runner.Request.RegistrationId,
            SourceActorId = "channel-agent-run:agent-run-target-1",
            Activity = BuildInboundActivity("msg-target-1"),
            Outbound = new MessageContent
            {
                Text = "{\"status\":\"pending\",\"reason\":\"AwaitingToolApproval\",\"success\":false}",
            },
            TerminalState = LlmReplyTerminalState.Completed,
            WorkflowRunDelivery = new WorkflowRunBackgroundDeliveryReceipt
            {
                DeliveryActorId = "workflow-delivery-actor-1",
                WorkflowActorId = "workflow-actor-1",
                WorkflowRunId = "workflow-run-1",
                WorkflowCommandId = "workflow-command-1",
                WorkflowCorrelationId = "workflow-correlation-1",
                StreamTopic = "aevatar://actors/workflow-actor-1/runs/workflow-command-1",
                ChannelPlatform = "lark",
                ReplyMessageId = "reply-message-1",
                PlatformMessageId = "platform-message-1",
                RegistrationScopeId = "registration-scope-1",
            },
            AppendedHistory =
            {
                new ConversationHistoryEntry
                {
                    Role = "tool",
                    ToolCallId = "call-start-workflow-1",
                    Content = "{\"status\":\"accepted\"}",
                },
            },
        });

        runner.ReplyRuntimeContexts.Should().BeEmpty();
        agent.State.PendingLlmReplyRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("llm:msg-target-1");

        var events = await eventStore.GetEventsAsync(actorId);
        var completed = events
            .Where(x => x.EventData.Is(ConversationTurnCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<ConversationTurnCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completed.WorkflowRunDelivery.DeliveryActorId.Should().Be("workflow-delivery-actor-1");
        completed.AppendedHistory.Should().ContainSingle(entry =>
            entry.Role == "tool" && entry.ToolCallId == "call-start-workflow-1");
        completed.Outbound.Should().BeNull();
        events.Should().NotContain(x => x.EventData.Is(LlmReplyDeliveredEvent.Descriptor));
        events.Should().NotContain(x => x.EventData.Is(DeliveryProducedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenNyxIdAuthorityIsOnlyDurableToolFact_PersistsItWithoutCredentials()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:dm:ou-channel-alpha");
        var runner = new DeferredReplyTurnRunner();
        runner.Request.ToolContext = (AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "owner-runtime-token",
                "owner-runtime-token",
                "sender-runtime-token"),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-authority-alpha",
                "ou-authority-alpha"),
        }).ToPayload();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher());

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-authority-only-1"));

        var persisted = agent.State.PendingLlmReplyRequests.Should().ContainSingle().Subject;
        var context = AgentToolExecutionContextMapper.FromPayload(persisted.ToolContext);
        context.NyxIdAuthority.Should().Be(new AgentToolNyxIdAuthorityContext(
            "lark",
            "tenant-authority-alpha",
            "ou-authority-alpha"));
        context.Credentials.Should().Be(AgentToolCredentials.Empty);
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_ShouldDispatchWorkflowDraftRunWithRuntimeCredentialsAndPersistScrubbedState()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var runner = new DeferredWorkflowDraftRunTurnRunner();
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var runtimeSecretStore = new InMemoryRuntimeSecretStore();
        var agent = await CreateAgentAsync(
            actorId,
            runner,
            new RecordingLlmReplyRunDispatcher(),
            dispatcher,
            eventStore,
            runtimeSecretStore: runtimeSecretStore);
        var activity = BuildInboundActivity("msg-workflow-1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "msg-workflow-1",
        };
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "runtime-user-token",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = activity,
            CorrelationId = "msg-workflow-1",
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        dispatcher.Requests.Should().ContainSingle();
        dispatcher.Requests[0].TargetActorId.Should().Be(actorId);
        dispatcher.Requests[0].ReplyToken.Should().Be("runtime-reply-token");
        dispatcher.Requests[0].NyxUserAccessToken.Should().Be("runtime-user-token");
        dispatcher.Requests[0].Activity.TransportExtras.NyxUserAccessToken.Should().Be("runtime-user-token");

        agent.State.PendingWorkflowDraftRunRequests.Should().ContainSingle();
        var persisted = agent.State.PendingWorkflowDraftRunRequests[0];
        persisted.TargetActorId.Should().Be(actorId);
        persisted.ReplyToken.Should().BeEmpty();
        persisted.ReplyTokenExpiresAtUnixMs.Should().Be(0);
        persisted.NyxUserAccessToken.Should().BeEmpty();
        persisted.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        persisted.RelayReplyTokenRef.Ref.Should().NotBeNullOrWhiteSpace();
        persisted.RelayUserAccessTokenRef.Ref.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_ShouldRetryAdmissionLocallyWhenRuntimeCredentialEnvelopeHitsOcc()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:dm:ou_user_1");
        var eventStore = new InMemoryEventStore
        {
            BeforeNextAppend = store => store.SeedExternalEvent(
                actorId,
                new ConversationReplyLifecycleChangedEvent
                {
                    CorrelationId = "other-turn",
                    ChangedAtUnixMs = 1,
                    TerminalReason = "concurrent_update",
                }),
        };
        var publisher = new RecordingEventPublisher();
        var agent = await CreateAgentAsync(
            actorId,
            new DeferredReplyTurnRunner(),
            new RecordingLlmReplyRunDispatcher(),
            workflowDispatcher: null,
            eventStore,
            publisher);
        var activity = BuildInboundActivity("msg-dm-occ-1");
        activity.Conversation = ConversationReference.Create(
            ChannelId.From("lark"),
            BotInstanceId.From("reg-1"),
            ConversationScope.DirectMessage,
            "ou_user_1",
            "dm",
            "ou_user_1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "om_dm_1",
            CorrelationId = "msg-dm-occ-1",
        };
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "runtime-user-token",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = activity,
            CorrelationId = "msg-dm-occ-1",
            RelayApiKeyId = "relay-key-1",
            CallbackJti = "callback-jti-1",
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            CallbackObservedAtUnixMs = 10,
            CallbackReplayExpiresAtUnixMs = 20,
        });

        agent.State.PendingRelayAdmissions.Should().ContainSingle(x =>
            x.ActivityId == "msg-dm-occ-1" &&
            x.RelayApiKeyId == "relay-key-1" &&
            x.CallbackJti == "callback-jti-1");
        eventStore.AppendAttempts.Should().Be(2);
        publisher.Sent.Should().ContainSingle();
        var sent = publisher.Sent[0];
        sent.TargetActorId.Should().Be(actorId);
        var turn = sent.Event.Should().BeOfType<NyxRelayCallbackTurnRequestedEvent>().Subject;
        turn.ActivityId.Should().Be("msg-dm-occ-1");
        turn.ReplyToken.Should().Be("runtime-reply-token");
        turn.NyxUserAccessToken.Should().Be("runtime-user-token");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_ShouldRejectWorkflowDraftRunWithoutRunIdBeforePersistenceOrDispatch()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var runner = new DeferredWorkflowDraftRunTurnRunner
        {
            ClearRunId = true,
        };
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher(), dispatcher, eventStore);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-workflow-1"));

        dispatcher.Requests.Should().BeEmpty();
        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        var failures = await ReadConversationFailuresAsync(eventStore, actorId);
        failures.Should().ContainSingle(x =>
            x.CommandId == "workflow-draft-run:msg-workflow-1" &&
            x.ErrorCode == "workflow_draft_run_missing_run_id_rejected" &&
            x.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_ShouldFailAndCleanWorkflowDraftRun_WhenDispatcherIsUnavailable()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var agent = await CreateAgentAsync(
            actorId,
            new DeferredWorkflowDraftRunTurnRunner(),
            new RecordingLlmReplyRunDispatcher(),
            workflowDispatcher: null,
            eventStore);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-workflow-1"));

        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        var failures = await ReadConversationFailuresAsync(eventStore, actorId);
        failures.Should().ContainSingle(x =>
            x.CommandId == "workflow-draft-run:msg-workflow-1" &&
            x.ErrorCode == "workflow_draft_run_interaction_port_unavailable" &&
            x.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_ShouldFailAndCleanWorkflowDraftRun_WhenDispatcherThrows()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort
        {
            ThrowOnDispatch = true,
        };
        var agent = await CreateAgentAsync(
            actorId,
            new DeferredWorkflowDraftRunTurnRunner(),
            new RecordingLlmReplyRunDispatcher(),
            dispatcher,
            eventStore);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-workflow-1"));

        dispatcher.Requests.Should().BeEmpty();
        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        var failures = await ReadConversationFailuresAsync(eventStore, actorId);
        failures.Should().ContainSingle(x =>
            x.CommandId == "workflow-draft-run:msg-workflow-1" &&
            x.ErrorCode == "workflow_draft_run_dispatch_failed" &&
            x.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task ActivateAsync_ShouldFailAndCleanScrubbedWorkflowDraftRunPendingState()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var runner = new DeferredWorkflowDraftRunTurnRunner();
        var firstDispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var firstAgent = await CreateAgentAsync(
            actorId,
            runner,
            new RecordingLlmReplyRunDispatcher(),
            firstDispatcher,
            eventStore);
        var activity = BuildInboundActivity("msg-workflow-1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "msg-workflow-1",
        };
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "runtime-user-token",
        };

        await firstAgent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = activity,
            CorrelationId = "msg-workflow-1",
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });
        firstDispatcher.Requests.Should().ContainSingle();

        var rehydrateDispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var rehydrated = await CreateAgentAsync(
            actorId,
            new IgnoredConversationTurnRunner(),
            new RecordingLlmReplyRunDispatcher(),
            rehydrateDispatcher,
            eventStore);

        rehydrateDispatcher.Requests.Should().BeEmpty();
        rehydrated.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        rehydrated.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        var events = await eventStore.GetEventsAsync(actorId);
        events
            .Where(x => x.EventData.TypeUrl.EndsWith(ConversationContinueFailedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<ConversationContinueFailedEvent>())
            .Should()
            .ContainSingle(x =>
                x.CommandId == "workflow-draft-run:msg-workflow-1" &&
                x.ErrorCode == "missing_runtime_reply_token" &&
                x.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_ShouldFailAndCleanWorkflowDraftRun_WhenUserTokenMissing()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var agent = await CreateAgentAsync(
            actorId,
            new DeferredWorkflowDraftRunTurnRunner(),
            new RecordingLlmReplyRunDispatcher(),
            dispatcher,
            eventStore);
        var activity = BuildInboundActivity("msg-workflow-1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-message-1",
            CorrelationId = "msg-workflow-1",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = activity,
            CorrelationId = "msg-workflow-1",
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        dispatcher.Requests.Should().BeEmpty();
        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        var failures = await ReadConversationFailuresAsync(eventStore, actorId);
        failures.Should().ContainSingle(x =>
            x.CommandId == "workflow-draft-run:msg-workflow-1" &&
            x.ErrorCode == "missing_runtime_user_access_token" &&
            x.RetryPolicyCase == ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_ShouldFinalizeWorkflowDraftRunPendingStateWithWorkflowRunId()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredWorkflowDraftRunTurnRunner();
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher(), dispatcher);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-workflow-1"));
        agent.State.PendingWorkflowDraftRunRequests.Should().ContainSingle();

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "msg-workflow-1",
            RunId = "workflow-draft-run-1",
            Activity = BuildInboundActivity("msg-workflow-1"),
            Outbound = new MessageContent { Text = "workflow done" },
            TerminalState = LlmReplyTerminalState.Completed,
            AppendedHistory =
            {
                new ConversationHistoryEntry
                {
                    Role = "assistant",
                    Content = "workflow done",
                },
            },
        });

        runner.LastReadyRunId.Should().Be("workflow-draft-run-1");
        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        agent.State.LastReplyDelivery.RunId.Should().Be("workflow-draft-run-1");
        agent.State.RetainedHistory.Should().ContainSingle().Which.Content.Should().Be("workflow done");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_ShouldCleanWorkflowDraftRunPendingStateOnTerminalFailure()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var runner = new DeferredWorkflowDraftRunTurnRunner
        {
            ReplyResult = ConversationTurnResult.PermanentFailure("delivery_failed", "reply failed"),
        };
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher(), dispatcher);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-workflow-1"));

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "msg-workflow-1",
            RunId = "workflow-draft-run-1",
            Activity = BuildInboundActivity("msg-workflow-1"),
            Outbound = new MessageContent { Text = "workflow failed" },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "workflow_failed",
            ErrorSummary = "workflow failed",
        });

        agent.State.PendingWorkflowDraftRunRequests.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("workflow-draft-run:msg-workflow-1");
        agent.State.LastReplyDelivery.RunId.Should().Be("workflow-draft-run-1");
        agent.State.LastReplyDelivery.Failed.ErrorCode.Should().Be("delivery_failed");
    }

    private static async Task<ConversationGAgent> CreateAgentAsync(
        string id,
        IConversationTurnRunner runner,
        IChannelLlmReplyRunDispatcher dispatcher,
        IChannelWorkflowDraftRunInteractionPort? workflowDispatcher = null,
        InMemoryEventStore? eventStore = null,
        RecordingEventPublisher? eventPublisher = null,
        IRuntimeSecretStore? runtimeSecretStore = null)
    {
        eventStore ??= new InMemoryEventStore();
        var servicesCollection = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, NoopActorDispatchPort>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddSingleton(runner)
            .AddSingleton(dispatcher)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (workflowDispatcher is not null)
            servicesCollection.AddSingleton(workflowDispatcher);
        if (runtimeSecretStore is not null)
            servicesCollection.AddSingleton(runtimeSecretStore);

        var services = servicesCollection.BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = eventPublisher ?? new RecordingEventPublisher(),
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        await agent.ActivateAsync();
        return agent;
    }

    private static async Task<IReadOnlyList<ConversationContinueFailedEvent>> ReadConversationFailuresAsync(
        InMemoryEventStore eventStore,
        string actorId)
    {
        var events = await eventStore.GetEventsAsync(actorId);
        return events
            .Where(x => x.EventData.TypeUrl.EndsWith(ConversationContinueFailedEvent.Descriptor.FullName, StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<ConversationContinueFailedEvent>())
            .ToList();
    }

    private static ChatActivity BuildInboundActivity(string messageId) =>
        new()
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            From = new ParticipantRef { CanonicalId = "ou_user_1" },
            Content = new MessageContent { Text = "hello" },
        };

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private sealed class DeferredReplyTurnRunner : IConversationTurnRunner
    {
        public NeedsLlmReplyEvent Request { get; } = new()
        {
            CorrelationId = "msg-target-1",
            RunId = "agent-run-target-1",
            RegistrationId = "reg-1",
            Activity = BuildInboundActivity("msg-target-1"),
            RequestedAtUnixMs = 10,
        };

        public List<ConversationTurnRuntimeContext> ReplyRuntimeContexts { get; } = [];

        public List<LlmReplyReadyEvent> Replies { get; } = [];

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.LlmReplyRequested(Request));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            ReplyRuntimeContexts.Add(runtimeContext);
            Replies.Add(reply.Clone());
            return Task.FromResult(ConversationTurnResult.Sent(
                "sent",
                reply.Outbound?.Clone() ?? new MessageContent(),
                "bot"));
        }

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId));
    }

    private sealed class DeferredWorkflowDraftRunTurnRunner : IConversationTurnRunner
    {
        private static readonly NeedsWorkflowDraftRunEvent Request = new()
        {
            CorrelationId = "msg-workflow-1",
            RunId = "workflow-draft-run-1",
            RegistrationId = "reg-1",
            Activity = BuildInboundActivity("msg-workflow-1"),
            WorkflowSource = new ChannelWorkflowDraftRunSource
            {
                Kind = ChannelWorkflowDraftRunSourceKind.DefinitionActor,
                ScopeId = "scope-1",
                WorkflowId = "daily-greeting",
                WorkflowName = "daily-greeting",
                DefinitionActorId = "workflow-actor-1",
            },
            Prompt = "/workflow run daily-greeting",
            RequestedAtUnixMs = 10,
        };

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            var request = Request.Clone();
            request.CorrelationId = activity.Id ?? request.CorrelationId;
            request.Activity = activity.Clone();
            if (ClearRunId)
                request.RunId = string.Empty;
            return Task.FromResult(ConversationTurnResult.WorkflowDraftRunRequested(request));
        }

        public string? LastReadyRunId { get; private set; }

        public bool ClearRunId { get; init; }

        public ConversationTurnResult ReplyResult { get; init; } = ConversationTurnResult.Sent(
            "sent",
            new MessageContent { Text = "sent" },
            "bot");

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            LastReadyRunId = reply.RunId;
            return Task.FromResult(ReplyResult);
        }

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId));
    }

    private sealed class IgnoredConversationTurnRunner : IConversationTurnRunner
    {
        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("ignored", activity.Id));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("ignored", reply.CorrelationId));

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("ignored", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId));
    }

    private sealed class RecordingLlmReplyRunDispatcher : IChannelLlmReplyRunDispatcher
    {
        public List<NeedsLlmReplyEvent> Requests { get; } = [];

        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowDraftRunInteractionPort : IChannelWorkflowDraftRunInteractionPort
    {
        public List<NeedsWorkflowDraftRunEvent> Requests { get; } = [];

        public bool ThrowOnDispatch { get; init; }

        public Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnDispatch)
                throw new InvalidOperationException("workflow draft-run dispatch failed");

            Requests.Add(request.Clone());
            return Task.CompletedTask;
        }

        public Task StartWorkflowInteractionAsync(string runActorId, NeedsWorkflowDraftRunEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sent.Add((targetActorId, (IMessage)evt.Descriptor.Parser.ParseFrom(evt.ToByteArray())));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);
        private bool _beforeNextAppendInvoked;

        public Action<InMemoryEventStore>? BeforeNextAppend { get; init; }

        public int AppendAttempts { get; private set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AppendAttempts++;
            if (!_beforeNextAppendInvoked && BeforeNextAppend is { } beforeNextAppend)
            {
                _beforeNextAppendInvoked = true;
                beforeNextAppend(this);
            }

            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public void SeedExternalEvent(string agentId, IMessage evt)
        {
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            stream.Add(new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Version = stream.Count == 0 ? 1 : stream[^1].Version + 1,
                EventType = evt.Descriptor.FullName,
                EventData = Any.Pack(evt),
                AgentId = agentId,
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
}
