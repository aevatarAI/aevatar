using System.Reflection;
using Aevatar.Foundation.Abstractions;
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

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_ShouldDispatchWorkflowDraftRunWithRuntimeCredentialsAndPersistScrubbedState()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:group:oc_group_chat_1");
        var eventStore = new InMemoryEventStore();
        var runner = new DeferredWorkflowDraftRunTurnRunner();
        var dispatcher = new RecordingWorkflowDraftRunInteractionPort();
        var agent = await CreateAgentAsync(actorId, runner, new RecordingLlmReplyRunDispatcher(), dispatcher, eventStore);
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
        InMemoryEventStore? eventStore = null)
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

        var services = servicesCollection.BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = new RecordingEventPublisher(),
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

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.LlmReplyRequested(Request));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Sent(
                "sent",
                reply.Outbound?.Clone() ?? new MessageContent(),
                "bot"));

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
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
            where TEvent : IMessage =>
            Task.CompletedTask;
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
