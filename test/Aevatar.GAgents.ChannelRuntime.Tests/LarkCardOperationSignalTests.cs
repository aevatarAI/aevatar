using System.Reflection;
using System.Text;
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
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkCardOperationSignalTests
{
    [Fact]
    public async Task LarkCardCreateTaskRun_DispatchesSignalOnlyPayload()
    {
        var dispatch = new RecordingActorDispatchPort();
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.PostSendFailed(
                "card_orphan",
                "om_orphan",
                "card_first_stream_failed",
                "stream rejected",
                isRateLimited: true),
        };
        var agent = CreateAgent("conv-lark-card-signal-only", runner, dispatch, new InMemoryEventStore());

        await agent.HandleEventAsync(Envelope("conv-lark-card-signal-only",
            CreateCardStreamChunk("corr-signal-only", "relay-msg-1", "hello")));

        var signal = await dispatch.WaitForPayloadAsync<LarkCardOperationCompletedEvent>();

        signal.Operation.Should().Be(LarkCardOperationPhase.Create);
        signal.OperationId.Should().StartWith("corr-signal-only:");
        signal.OperationId.Should().EndWith(":1:1");
        signal.State.Should().Be(LarkCardOperationResultState.Failed);
        signal.RawResult.CardId.Should().Be("card_orphan");
        signal.RawResult.CardMessageId.Should().Be("om_orphan");
        signal.RawResult.RawErrorCode.Should().Be("card_first_stream_failed");
        signal.RawResult.RawErrorSummary.Should().Be("stream rejected");
        signal.RawResult.IsRateLimited.Should().BeTrue();
        signal.RawResult.IsPostSendFailure.Should().BeTrue();
        signal.Should().BeEquivalentTo(signal.Clone());
    }

    [Fact]
    public async Task LarkCardCreateSelfDispatch_AdvancesStreamingStateThroughPipeline()
    {
        var dispatch = new RecordingActorDispatchPort();
        var store = new InMemoryEventStore();
        var agent = CreateAgent(
            "conv-lark-card-self-dispatch",
            new RecordingCardRunner(),
            dispatch,
            store);

        await agent.HandleEventAsync(Envelope(agent.Id,
            CreateCardStreamChunk("corr-self-dispatch", "relay-msg-1", "hello")));

        var completionEnvelope = await dispatch.WaitForEnvelopeAsync<LarkCardOperationCompletedEvent>();
        completionEnvelope.Route.PublisherActorId.Should().Be(agent.Id);
        completionEnvelope.Route.Direct.TargetActorId.Should().Be(agent.Id);

        await agent.HandleEventAsync(completionEnvelope);

        var lifecycle = agent.State.ActiveReplyLifecycles.Single();
        lifecycle.Phase.Should().Be(ConversationReplyLifecyclePhase.LarkCardStreaming);
        lifecycle.CardId.Should().Be("card_ok");
        lifecycle.CardMessageId.Should().Be("om_card_msg");
        lifecycle.LarkCardInFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        lifecycle.LastFlushedText.Should().Be("hello");

        var events = await store.GetEventsAsync(agent.Id);
        events
            .Select(e => e.EventType)
            .Should()
            .Contain(ConversationReplyLifecycleChangedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task LarkCardOperationCompleted_ActorReconstructsRichContinuation()
    {
        var store = new InMemoryEventStore();
        var agent = CreateAgent(
            "conv-lark-card-reconstruct",
            new RecordingCardRunner(),
            new RecordingActorDispatchPort(),
            store);

        await agent.HandleEventAsync(Envelope("conv-lark-card-reconstruct",
            CreateCardStreamChunk("corr-reconstruct", "relay-msg-1", "hello")));
        var lifecycle = agent.State.ActiveReplyLifecycles.Single();

        await agent.HandleEventAsync(Envelope("conv-lark-card-reconstruct",
            new LarkCardOperationCompletedEvent
            {
                OperationId = "corr-reconstruct:create:1:1",
                CorrelationId = "corr-reconstruct",
                Operation = LarkCardOperationPhase.Create,
                Sequence = lifecycle.LarkCardInFlightSequence,
                OperationGeneration = lifecycle.LarkCardOperationGeneration,
                State = LarkCardOperationResultState.Failed,
                Chunk = CreateCardStreamChunk("corr-reconstruct", "relay-msg-1", "hello"),
                RawResult = new LarkCardOperationRawResult
                {
                    CardId = "card_orphan",
                    CardMessageId = "om_orphan",
                    IsPostSendFailure = true,
                    RawErrorCode = "card_first_stream_failed",
                    RawErrorSummary = "stream rejected",
                },
            }));

        var events = await store.GetEventsAsync(agent.Id);
        var changed = events
            .Where(e => e.EventType == ConversationReplyLifecycleChangedEvent.Descriptor.FullName)
            .Select(e => ConversationReplyLifecycleChangedEvent.Parser.ParseFrom(e.EventData.Value))
            .Last();
        changed.CorrelationId.Should().Be("corr-reconstruct");
        changed.Mode.Should().Be(ConversationReplyLifecycleMode.LarkCard);
        changed.PreviousPhase.Should().Be(ConversationReplyLifecyclePhase.LarkCardCreating);
        changed.Phase.Should().Be(ConversationReplyLifecyclePhase.LarkCardTerminated);
        changed.ChangedAtUnixMs.Should().BeGreaterThan(0);
        changed.CardIdAssigned.Should().Be("card_orphan");
        changed.CardMessageIdAssigned.Should().Be("om_orphan");
        changed.OriginalCardIdAssigned.Should().Be("card_orphan");
        changed.LarkCardOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        changed.OperationSequence.Should().Be(0);
        changed.OperationGeneration.Should().Be(lifecycle.LarkCardOperationGeneration);
        changed.TerminalReason.Should().Be("create_post_send_failed:card_first_stream_failed");

        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        completed.SentActivityId.Should().Be("lark-card-stream:om_orphan");
    }

    [Fact]
    public async Task HandleLlmReplyCardStreamChunkAsync_ScheduledTimeoutPayload_StripsRuntimeRelayCredentials()
    {
        await using var callbackHarness = await RuntimeCallbackSchedulerGrainTestHarness.StartAsync();
        var agent = CreateAgent(
            "conv-lark-card-timeout-sanitize",
            new RecordingCardRunner(),
            new RecordingActorDispatchPort(),
            new InMemoryEventStore(),
            callbackHarness.Scheduler);

        await agent.HandleEventAsync(Envelope("conv-lark-card-timeout-sanitize",
            CreateCardStreamChunk("corr-card-timeout-token", "relay-msg-1", "hello")));

        var scheduled = callbackHarness.Timeouts.Should().ContainSingle().Subject;
        var timeout = scheduled.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>();
        timeout.CorrelationId.Should().Be("corr-card-timeout-token");
        timeout.Operation.Should().Be(LarkCardOperationPhase.Create);

        var persistedText = Encoding.UTF8.GetString(scheduled.TriggerEnvelope.ToByteArray());
        persistedText.Should().NotContain("runtime-token-corr-card-timeout-token");
        persistedText.Should().NotContain("runtime-user-access-token-corr-card-timeout-token");
        persistedText.Should().NotContain("reply_token");
        persistedText.Should().NotContain("reply_token_expires_at_unix_ms");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_FinalizeTimeoutPayload_StripsActivityRuntimeRelayCredentials()
    {
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(
            "conv-lark-card-finalize-timeout-sanitize",
            new RecordingCardRunner(),
            new RecordingActorDispatchPort(),
            new InMemoryEventStore(),
            scheduler);
        var chunk = CreateCardStreamChunk("corr-card-finalize-token", "relay-msg-1", "hello");

        await agent.HandleEventAsync(Envelope(agent.Id, chunk));
        var lifecycle = agent.State.ActiveReplyLifecycles.Single();
        await agent.HandleEventAsync(Envelope(agent.Id,
            new LarkCardOperationCompletedEvent
            {
                OperationId = "corr-card-finalize-token:create:1:1",
                CorrelationId = "corr-card-finalize-token",
                Operation = LarkCardOperationPhase.Create,
                Sequence = lifecycle.LarkCardInFlightSequence,
                OperationGeneration = lifecycle.LarkCardOperationGeneration,
                State = LarkCardOperationResultState.Succeeded,
                Chunk = chunk.Clone(),
                RawResult = new LarkCardOperationRawResult
                {
                    CardId = "card_ok",
                    CardMessageId = "om_card_msg",
                },
            }));
        scheduler.Timeouts.Clear();

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-card-finalize-token",
            RegistrationId = "reg-1",
            SourceActorId = "agent-run",
            Activity = chunk.Activity.Clone(),
            Outbound = new MessageContent { Text = "final text" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyToken = "runtime-ready-token-corr-card-finalize-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            ReadyAtUnixMs = 100,
        });

        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;
        var timeout = scheduled.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>();
        timeout.Operation.Should().Be(LarkCardOperationPhase.Finalize);
        timeout.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        timeout.Activity.TransportExtras.NyxAgentApiKeyId.Should().Be("nyx-key-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxPlatform.Should().Be("lark");
        timeout.Activity.TransportExtras.NyxConversationId.Should().Be("oc-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxPlatformMessageId.Should().Be("om-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxLarkUnionId.Should().Be("on-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc-lark-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxRegistrationScopeId.Should().Be("scope-corr-card-finalize-token");
        timeout.Activity.TransportExtras.NyxSenderUserId.Should().Be("user-corr-card-finalize-token");

        var persistedText = Encoding.UTF8.GetString(scheduled.TriggerEnvelope.ToByteArray());
        persistedText.Should().NotContain("runtime-user-access-token-corr-card-finalize-token");
        persistedText.Should().NotContain("runtime-ready-token-corr-card-finalize-token");
        persistedText.Should().NotContain("nyx_user_access_token");
        persistedText.Should().NotContain("reply_token");

        await using var callbackHarness = await RuntimeCallbackSchedulerGrainTestHarness.StartAsync();
        await callbackHarness.Scheduler.ScheduleTimeoutAsync(new RuntimeCallbackTimeoutRequest
        {
            ActorId = agent.Id,
            CallbackId = "lark-card-finalize-timeout-sanitized",
            TriggerEnvelope = scheduled.TriggerEnvelope.Clone(),
            DueTime = TimeSpan.FromMinutes(1),
        });
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenCardCreateInFlight_QueuesFailureTextAndFinalizesAfterCreate()
    {
        var scheduler = new RecordingCallbackScheduler();
        var runner = new RecordingCardRunner();
        var agent = CreateAgent(
            "conv-lark-card-create-inflight-failure-finalize",
            runner,
            new RecordingActorDispatchPort(),
            new InMemoryEventStore(),
            scheduler);
        var chunk = CreateCardStreamChunk("corr-card-create-inflight-failure", "relay-msg-1", "...");

        await agent.HandleEventAsync(Envelope(agent.Id, chunk));
        var lifecycle = agent.State.ActiveReplyLifecycles.Single();

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = chunk.CorrelationId,
            RegistrationId = "reg-1",
            SourceActorId = "agent-run",
            Activity = chunk.Activity.Clone(),
            Outbound = new MessageContent { Text = "Sorry, I couldn't complete this reply. Please try again." },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = "provider failed",
            ReplyToken = "runtime-ready-token-" + chunk.CorrelationId,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            ReadyAtUnixMs = 100,
        });

        var queued = agent.State.ActiveReplyLifecycles.Single();
        queued.Phase.Should().Be(ConversationReplyLifecyclePhase.LarkCardCreating);
        queued.PendingFinalizeText.Should().Be("Sorry, I couldn't complete this reply. Please try again.");
        queued.PendingFinalizeCommandId.Should().Be("llm:corr-card-create-inflight-failure");

        await agent.HandleEventAsync(Envelope(agent.Id,
            new LarkCardOperationCompletedEvent
            {
                OperationId = "corr-card-create-inflight-failure:create:1:1",
                CorrelationId = chunk.CorrelationId,
                Operation = LarkCardOperationPhase.Create,
                Sequence = lifecycle.LarkCardInFlightSequence,
                OperationGeneration = lifecycle.LarkCardOperationGeneration,
                State = LarkCardOperationResultState.Succeeded,
                Chunk = chunk.Clone(),
                RawResult = new LarkCardOperationRawResult
                {
                    CardId = "card_ok",
                    CardMessageId = "om_card_msg",
                },
            }));

        var finalizeCall = await runner.WaitForFinalizeCallAsync();
        runner.FinalizeCalls.Should().ContainSingle();
        finalizeCall.FinalText.Should().Be("Sorry, I couldn't complete this reply. Please try again.");
        finalizeCall.FinalTextDiffersFromLastFlushed.Should().BeTrue();

        var finalizing = agent.State.ActiveReplyLifecycles.Single();
        finalizing.Phase.Should().Be(ConversationReplyLifecyclePhase.LarkCardStreaming);
        finalizing.LarkCardInFlightOperation.Should().Be(LarkCardOperationPhase.Finalize);

        await agent.HandleEventAsync(Envelope(agent.Id,
            new LarkCardOperationCompletedEvent
            {
                OperationId = "corr-card-create-inflight-failure:finalize:2:2",
                CorrelationId = chunk.CorrelationId,
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = finalizing.LarkCardInFlightSequence,
                OperationGeneration = finalizing.LarkCardOperationGeneration,
                State = LarkCardOperationResultState.Succeeded,
                CardId = "card_ok",
                CardMessageId = "om_card_msg",
                CommandId = "llm:corr-card-create-inflight-failure",
                Activity = chunk.Activity.Clone(),
                FinalText = "Sorry, I couldn't complete this reply. Please try again.",
                LastFlushedText = "...",
                RawResult = new LarkCardOperationRawResult { FinalTextWritten = true },
            }));

        agent.State.ActiveReplyLifecycles.Should().BeEmpty();
        agent.State.ProcessedCommandIds.Should().Contain("llm:corr-card-create-inflight-failure");
    }

    private static ConversationGAgent CreateAgent(
        string id,
        IConversationCardTurnRunner cardRunner,
        IActorDispatchPort dispatch,
        IEventStore store,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(dispatch)
            .AddSingleton(cardRunner)
            .AddSingleton(callbackScheduler ?? new NoopCallbackScheduler())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        agent.EventPublisher = new SelfHandlingEventPublisher(agent);
        agent.ActivateAsync().GetAwaiter().GetResult();
        return agent;
    }

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
                    LarkCardOperationCompletedEvent signal => signal.CorrelationId,
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

    private static LlmReplyCardStreamChunkEvent CreateCardStreamChunk(
        string correlationId,
        string replyMessageId,
        string accumulatedText) =>
        new()
        {
            CorrelationId = correlationId,
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = correlationId,
                Type = ActivityType.Message,
                ChannelId = new ChannelId { Value = "lark" },
                Bot = new BotInstanceId { Value = "lark-bot" },
                Conversation = new ConversationReference
                {
                    Channel = new ChannelId { Value = "lark" },
                    Bot = new BotInstanceId { Value = "lark-bot" },
                    Scope = ConversationScope.Group,
                    CanonicalKey = "conv:lark:grp",
                },
                Content = new MessageContent { Text = "user question" },
                OutboundDelivery = new OutboundDeliveryContext
                {
                    ReplyMessageId = replyMessageId,
                    CorrelationId = correlationId,
                },
                TransportExtras = new TransportExtras
                {
                    NyxUserAccessToken = "runtime-user-access-token-" + correlationId,
                    NyxAgentApiKeyId = "nyx-key-" + correlationId,
                    NyxPlatform = "lark",
                    NyxConversationId = "oc-" + correlationId,
                    NyxPlatformMessageId = "om-" + correlationId,
                    NyxLarkUnionId = "on-" + correlationId,
                    NyxLarkChatId = "oc-lark-" + correlationId,
                    NyxRegistrationScopeId = "scope-" + correlationId,
                    NyxSenderUserId = "user-" + correlationId,
                },
            },
            AccumulatedText = accumulatedText,
            ChunkAtUnixMs = 42,
            ReplyToken = "runtime-token-" + correlationId,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private sealed class RecordingCardRunner : IConversationCardTurnRunner
    {
        private readonly TaskCompletionSource<(string FinalText, bool FinalTextDiffersFromLastFlushed, long Sequence)> _finalizeCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConversationCardCreateResult CreateResult { get; init; } =
            ConversationCardCreateResult.Succeeded("card_ok", "om_card_msg");

        public List<(string FinalText, bool FinalTextDiffersFromLastFlushed, long Sequence)> FinalizeCalls { get; } = [];

        public async Task<(string FinalText, bool FinalTextDiffersFromLastFlushed, long Sequence)> WaitForFinalizeCallAsync() =>
            await _finalizeCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task<ConversationCardCreateResult> RunCardCreateAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string streamingElementId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(CreateResult);

        public Task<ConversationCardStreamResult> RunCardStreamAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string cardId,
            string elementId,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationCardStreamResult.Succeeded());

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
            var call = (finalText, finalTextDiffersFromLastFlushed, sequence);
            FinalizeCalls.Add(call);
            _finalizeCall.TrySetResult(call);
            return Task.FromResult(ConversationCardFinalizeResult.Succeeded());
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly TaskCompletionSource<EventEnvelope> _dispatched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _dispatched.TrySetResult(envelope.Clone());
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public async Task<T> WaitForPayloadAsync<T>()
            where T : IMessage<T>, new()
        {
            var envelope = await WaitForEnvelopeAsync<T>();
            return envelope.Payload.Unpack<T>();
        }

        public async Task<EventEnvelope> WaitForEnvelopeAsync<T>()
            where T : IMessage<T>, new()
        {
            var envelope = await _dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            envelope.Payload.Unpack<T>();
            return envelope;
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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
                throw new EventStoreOptimisticConcurrencyException(
                    agentId,
                    expectedVersion,
                    currentVersion);

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

    private sealed class SelfHandlingEventPublisher(ConversationGAgent agent) : IEventPublisher
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
            string.Equals(targetActorId, agent.Id, StringComparison.Ordinal)
                ? agent.HandleEventAsync(Envelope(targetActorId, evt))
                : Task.CompletedTask;
    }
}
