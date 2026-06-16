using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunLarkCardDeliveryTests
{
    [Fact]
    public async Task CardChunkEnvelope_StartsCreateOnRunActorState()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("hello")));

        agent.State.LarkCardDelivery.Should().NotBeNull();
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Creating);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Create);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(1);
        agent.State.LarkCardDelivery.OperationGeneration.Should().Be(1);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().Be("hello");
        publisher.Sent.Should().ContainSingle(e => e.Event is ReplyOperationStepEvent);
        publisher.Sent.OfType<(string TargetActorId, IMessage Event)>()
            .Where(e => e.Event is ReplyOperationStepEvent)
            .Select(e => e.TargetActorId)
            .Should().ContainSingle(agent.Id);
        runner.CreateCalls.Should().BeEmpty("publisher dispatch records the self-message before IO runs");
    }

    [Fact]
    public async Task CreateAndStreamCompletions_MaintainMonotonicCardSequenceAndLatestCoalescedText()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("first")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Streaming);
        agent.State.LarkCardDelivery.CardId.Should().Be("card-ok");
        agent.State.LarkCardDelivery.CardMessageId.Should().Be("om-card-ok");
        agent.State.LarkCardDelivery.Sequence.Should().Be(1);
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("first");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("second")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("third")));

        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Stream);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(2);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().Be("third");
        await DispatchSelfEventCountAsync(agent, publisher, 2);

        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("second");
        agent.State.LarkCardDelivery.Sequence.Should().Be(2);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Stream);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(3);
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("third");
        agent.State.LarkCardDelivery.Sequence.Should().Be(3);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().BeEmpty();
        runner.StreamCalls.Select(call => call.Sequence).Should().Equal(2, 3);
    }

    [Fact]
    public async Task FinalizeAfterVisibleCard_DispatchesCompletionToConversationAndHandsOffRun()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        var ready = CreateReady("final", activityAccessToken: "ready-user-access-token");
        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = ready,
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });

        agent.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Finalize);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(2);
        var finalizeStep = publisher.Sent
            .Select(e => e.Event)
            .OfType<ReplyOperationStepEvent>()
            .Last(step => step.LarkCard.Operation == LarkCardOperationPhase.Finalize);
        finalizeStep.LarkCard.Activity.TransportExtras.NyxUserAccessToken.Should()
            .Be("ready-user-access-token");

        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Completed);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runner.FinalizeCalls.Should().ContainSingle();
        runner.FinalizeCalls[0].RuntimeUserAccessToken.Should().Be("ready-user-access-token");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.CorrelationId.Should().Be("corr-card");
        completed.RunId.Should().Be("run-1");
        completed.CommandId.Should().Be("llm:corr-card");
        completed.CardMessageId.Should().Be("om-card-ok");
        completed.OutboundText.Should().Be("final");
        completed.DeliveryFailure.Should().BeNull();
        scheduler.Timeouts.Should().Contain(timeout => timeout.CallbackId.StartsWith(
            "agent-run-terminal-cleanup:run-1",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizeFailure_PreservesLastVisibleTextAndSendsDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            FinalizeResult = ConversationCardFinalizeResult.Failed(
                "card_close_failed",
                "close rejected",
                finalTextWritten: false),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = CreateReady("final"),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("partial");
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("card_close_failed");
    }

    [Fact]
    public async Task CreateFailure_FallsBackToConversationTextChunk()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("fallback text")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        var fallback = publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Single();
        fallback.CorrelationId.Should().Be("corr-card");
        fallback.AccumulatedText.Should().Be("fallback text");
        publisher.Sent.Where(e => e.Event is LlmReplyStreamChunkEvent)
            .Select(e => e.TargetActorId)
            .Should().ContainSingle("conversation-1");
    }

    [Fact]
    public async Task TimeoutPayloads_DoNotPersistRuntimeCredentials()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(new RecordingCardRunner(), publisher: publisher, scheduler: scheduler);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));

        var createTimeout = scheduler.Timeouts.Should().ContainSingle().Subject;
        var createText = Encoding.UTF8.GetString(createTimeout.TriggerEnvelope.ToByteArray());
        createText.Should().NotContain("runtime-reply-token");
        createText.Should().NotContain("runtime-user-access-token");
        createText.Should().NotContain("reply_token");
        createText.Should().NotContain("nyx_user_access_token");

        await DispatchPendingSelfEventsAsync(agent, publisher);
        scheduler.Timeouts.Clear();

        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = CreateReady("final", activityAccessToken: "ready-user-access-token"),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });

        var finalizeTimeout = scheduler.Timeouts.Should().ContainSingle().Subject;
        var timeout = finalizeTimeout.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>();
        timeout.Operation.Should().Be(LarkCardOperationPhase.Finalize);
        timeout.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        var finalizeText = Encoding.UTF8.GetString(finalizeTimeout.TriggerEnvelope.ToByteArray());
        finalizeText.Should().NotContain("ready-user-access-token");
        finalizeText.Should().NotContain("runtime-reply-token");
        finalizeText.Should().NotContain("nyx_user_access_token");
        finalizeText.Should().NotContain("reply_token");
    }

    [Fact]
    public void AgentRunLarkCardDeliveryStateAndEvent_RoundtripTypedContract()
    {
        var state = new AgentRunGAgentState
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            LarkCardDelivery = new AgentRunLarkCardDeliveryState
            {
                Phase = AgentRunLarkCardDeliveryPhase.Streaming,
                CardId = "card-1",
                CardMessageId = "om-1",
                OriginalCardId = "card-1",
                LastFlushedText = "partial",
                Sequence = 4,
                StreamingElementId = "streaming_main",
                InFlightOperation = LarkCardOperationPhase.Finalize,
                InFlightSequence = 5,
                OperationGeneration = 6,
                PendingFinalizeText = "final",
                PendingFinalizeCommandId = "llm:corr-card",
            },
        };
        state.LarkCardDelivery.PendingAppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "assistant",
            Content = "final",
        });
        var evt = new AgentRunLarkCardDeliveryChangedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            ChangedAtUnixMs = 42,
            PreviousPhase = AgentRunLarkCardDeliveryPhase.Streaming,
            Phase = AgentRunLarkCardDeliveryPhase.Completed,
            InFlightOperation = LarkCardOperationPhase.Unspecified,
            OperationSequence = 0,
            OperationGeneration = 6,
            CardIdAssigned = "card-1",
            CardMessageIdAssigned = "om-1",
            FlushedText = "final",
            Sequence = 5,
            TerminalReason = "completed",
        };

        AgentRunGAgentState.Parser.ParseFrom(state.ToByteArray()).Should().Be(state);
        AgentRunLarkCardDeliveryChangedEvent.Parser.ParseFrom(evt.ToByteArray()).Should().Be(evt);
    }

    private static AgentRunGAgent CreateAgent(
        RecordingCardRunner runner,
        RecordingEventPublisher? publisher = null,
        RecordingCallbackScheduler? scheduler = null)
    {
        var actorRuntime = new RecordingActorRuntime();
        var executor = new NoopReplyGenerationExecutor();
        var callbackScheduler = scheduler ?? new RecordingCallbackScheduler();
        var agent = new AgentRunGAgent(
            actorRuntime,
            executor,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
            },
            NullLogger<AgentRunGAgent>.Instance,
            callbackScheduler);
        SetId(agent, "agent-run-actor-1");
        var services = new ServiceCollection()
            .AddSingleton<IConversationCardTurnRunner>(runner)
            .AddSingleton<IActorRuntimeCallbackScheduler>(callbackScheduler)
            .BuildServiceProvider();
        agent.Services = services;
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeTransition(agent, current, evt));
        publisher ??= new RecordingEventPublisher();
        publisher.SelfTarget = agent;
        agent.EventPublisher = publisher;
        SetState(agent, new AgentRunGAgentState
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-1",
                CorrelationId = "corr-card",
                TargetActorId = "conversation-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });
        return agent;
    }

    private static async Task DispatchPendingSelfEventsAsync(
        AgentRunGAgent agent,
        RecordingEventPublisher publisher)
    {
        while (publisher.TryDequeueSelfEvent(out var evt))
            await agent.HandleEventAsync(Envelope(agent.Id, evt));
    }

    private static async Task DispatchSelfEventCountAsync(
        AgentRunGAgent agent,
        RecordingEventPublisher publisher,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            publisher.TryDequeueSelfEvent(out var evt).Should().BeTrue();
            await agent.HandleEventAsync(Envelope(agent.Id, evt));
        }
    }

    private static NeedsLlmReplyEvent CreateReady(
        string finalText,
        string activityAccessToken = "runtime-user-access-token") =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            RegistrationId = "reg-1",
            Activity = CreateActivity(activityAccessToken),
            ReplyToken = "runtime-ready-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static LlmReplyCardStreamChunkEvent CreateCardChunk(string text) =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            RegistrationId = "reg-1",
            Activity = CreateActivity("runtime-user-access-token"),
            AccumulatedText = text,
            ChunkAtUnixMs = 42,
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static ChatActivity CreateActivity(string userAccessToken) =>
        new()
        {
            Id = "msg-1",
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc-group-1",
                "group",
                "oc-group-1"),
            Content = new MessageContent { Text = "question" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-card",
            },
            TransportExtras = new TransportExtras
            {
                NyxUserAccessToken = userAccessToken,
                NyxAgentApiKeyId = "api-key-1",
                NyxPlatform = "lark",
                NyxConversationId = "oc-group-1",
                NyxPlatformMessageId = "om-source",
                NyxLarkUnionId = "on-user",
                NyxLarkChatId = "oc-group-1",
                NyxRegistrationScopeId = "reg-scope-1",
                NyxSenderUserId = "nyx-user-1",
            },
        };

    private static EventEnvelope Envelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = payload switch
                {
                    LlmReplyCardStreamChunkEvent chunk => chunk.CorrelationId,
                    LarkCardOperationCompletedEvent completed => completed.CorrelationId,
                    ReplyOperationStepEvent step => step.CorrelationId,
                    _ => string.Empty,
                },
            },
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

    private static void SetState(AgentRunGAgent agent, AgentRunGAgentState state)
    {
        var stateField = typeof(Aevatar.Foundation.Core.GAgentBase<AgentRunGAgentState>).GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic);
        stateField.Should().NotBeNull();
        stateField!.SetValue(agent, state);
    }

    private static AgentRunGAgentState InvokeTransition(
        AgentRunGAgent agent,
        AgentRunGAgentState current,
        IMessage evt)
    {
        var currentType = agent.GetType();
        while (currentType is not null)
        {
            var transitionMethod = currentType.GetMethod(
                "TransitionState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (transitionMethod is not null)
                return (AgentRunGAgentState)transitionMethod.Invoke(agent, [current, evt])!;

            currentType = currentType.BaseType;
        }

        throw new InvalidOperationException("Unable to invoke AgentRunGAgent transition via reflection.");
    }

    private sealed class RecordingCardRunner : IConversationCardTurnRunner
    {
        public ConversationCardCreateResult CreateResult { get; init; } =
            ConversationCardCreateResult.Succeeded("card-ok", "om-card-ok");

        public ConversationCardStreamResult StreamResult { get; init; } =
            ConversationCardStreamResult.Succeeded();

        public ConversationCardFinalizeResult FinalizeResult { get; init; } =
            ConversationCardFinalizeResult.Succeeded();

        public List<LlmReplyCardStreamChunkEvent> CreateCalls { get; } = [];

        public List<(string Text, long Sequence)> StreamCalls { get; } = [];

        public List<(string FinalText, long Sequence, string ActivityUserAccessToken, string RuntimeUserAccessToken)> FinalizeCalls { get; } = [];

        public Task<ConversationCardCreateResult> RunCardCreateAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string streamingElementId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            CreateCalls.Add(chunk.Clone());
            return Task.FromResult(CreateResult);
        }

        public Task<ConversationCardStreamResult> RunCardStreamAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string cardId,
            string elementId,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            StreamCalls.Add((chunk.AccumulatedText, sequence));
            return Task.FromResult(StreamResult);
        }

        public Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
            ChatActivity referenceActivity,
            string cardId,
            string elementId,
            string finalText,
            bool finalTextDiffersFromLastFlushed,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            FinalizeCalls.Add((
                finalText,
                sequence,
                referenceActivity.TransportExtras?.NyxUserAccessToken ?? string.Empty,
                runtimeContext.NyxUserAccessToken ?? string.Empty));
            return Task.FromResult(FinalizeResult);
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        private readonly Queue<IMessage> _pendingSelfEvents = new();

        public AgentRunGAgent? SelfTarget { get; set; }

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
            var clone = (IMessage)evt.Descriptor.Parser.ParseFrom(evt.ToByteArray());
            Sent.Add((targetActorId, clone));
            if (SelfTarget is not null &&
                string.Equals(targetActorId, SelfTarget.Id, StringComparison.Ordinal) &&
                clone is ReplyOperationStepEvent or LarkCardOperationCompletedEvent)
            {
                _pendingSelfEvents.Enqueue(clone);
            }

            return Task.CompletedTask;
        }

        public bool TryDequeueSelfEvent(out IMessage evt) =>
            _pendingSelfEvents.TryDequeue(out evt!);
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

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

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunReplyStepState());

        public Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StateTransitionEventSourcing<TState>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                LatestVersion = CurrentVersion,
            });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
    }
}
