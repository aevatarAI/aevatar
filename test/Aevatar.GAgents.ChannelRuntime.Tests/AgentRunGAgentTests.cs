using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunGAgentTests
{
    [Fact]
    public async Task DispatchAsync_ShouldCreateRunActorAndDispatchStartCommand()
    {
        var actorRuntime = new DispatchingActorRuntime();
        var streamProvider = new RecordingStreamProvider();
        var dispatcher = new AgentRunDispatcher(
            actorRuntime,
            streamProvider,
            NullLogger<AgentRunDispatcher>.Instance);

        await dispatcher.DispatchAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-dispatch",
            TargetActorId = "conversation-actor",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-dispatch",
        }, CancellationToken.None);

        streamProvider.Produced.Should().ContainSingle();
        var (actorId, envelope) = streamProvider.Produced.Single();
        actorId.Should().Be(AgentRunGAgent.BuildActorId("corr-dispatch"));
        envelope.Propagation.CorrelationId.Should().Be("corr-dispatch");
        var command = envelope.Payload.Unpack<AgentRunStartRequested>();
        command.Request.CorrelationId.Should().Be("corr-dispatch");
        command.Request.TargetActorId.Should().Be("conversation-actor");
        command.Request.ReplyToken.Should().Be("relay-token-dispatch");
    }

    [Fact]
    public void ApplyReplyProduced_HistoricalEventWithoutReplyText_MarksAsAlreadyDispatched()
    {
        // Backward-compat for pre-refactor live state: AgentRunReplyProducedEvents persisted
        // by the old code path have no reply_text / outbound / terminal_state fields (proto3
        // defaults on deserialize). The old code only wrote this event AFTER a successful
        // dispatch, so on replay we MUST treat these as ReplyDispatched=true. Otherwise:
        //   1. HandleStartAsync would fire ReDispatchProducedReplyAsync with an empty payload
        //      (would surface as a blank or structural-error reply).
        //   2. HandleCleanupAsync would refuse to destroy the actor, leaking grain state.
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var historical = new AgentRunReplyProducedEvent
        {
            RunId = "run-historic",
            CorrelationId = "corr-historic",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // ReplyText, Outbound, TerminalState intentionally left default — this is the
            // shape proto3 deserialization gives for an event persisted before those fields
            // existed.
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), historical);

        // Legacy events get promoted straight to handed-off on replay (ADR-0021):
        // historically a ReplyProduced event was only persisted *after* successful
        // dispatch, so on replay we treat the event as if dispatch had also landed.
        next.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
    }

    [Fact]
    public void ApplyReplyProduced_NewInteractiveOnlyEvent_EmptyReplyText_ButNonNullOutbound_IsNotMisclassifiedAsHistorical()
    {
        // Interactive-only turns (reply_with_interaction, card-only intents) produce an
        // empty reply_text but a non-null outbound (card / button payload). The historical-
        // event discriminator MUST require BOTH empty reply_text AND null outbound,
        // otherwise this event would be marked ReplyDispatched=true on replay and
        // ReDispatchProducedReplyAsync would never fire after a failed dispatch — the user
        // would silently lose the interactive reply.
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var interactiveCard = new MessageContent { Text = string.Empty };
        interactiveCard.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "confirm",
            Label = "Confirm",
            IsPrimary = true,
        });

        var interactiveOnly = new AgentRunReplyProducedEvent
        {
            RunId = "run-interactive",
            CorrelationId = "corr-interactive",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyText = string.Empty, // intentionally empty — interactive-only turn
            Outbound = interactiveCard,
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), interactiveOnly);

        // Interactive-only fresh event: payload persisted, but status stays at
        // REPLY_PRODUCED until ApplyReplyDispatched promotes it to REPLY_HANDED_OFF.
        next.Status.Should().Be(AgentRunStatus.ReplyProduced);
        next.ProducedReplyText.Should().BeEmpty();
        next.ProducedOutbound.Should().NotBeNull();
        next.ProducedOutbound!.Actions.Should().ContainSingle(a => a.ActionId == "confirm");
    }

    [Fact]
    public void ApplyReplyProduced_NewEventWithReplyText_LeavesStatusAtReplyProduced()
    {
        // New events always carry a non-empty reply_text (empty replies get replaced with a
        // user-visible fallback before persisting). Those events represent "payload persisted
        // but not yet handed off" — Status stays at REPLY_PRODUCED here; the subsequent
        // AgentRunReplyDispatchedEvent promotes it to REPLY_HANDED_OFF after the
        // conversation actor accepts the LlmReplyReadyEvent (ADR-0021).
        var runtime = CreateRunAgent(
            new DispatchingActorRuntime(),
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var fresh = new AgentRunReplyProducedEvent
        {
            RunId = "run-fresh",
            CorrelationId = "corr-fresh",
            TargetActorId = "actor-1",
            ProducedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TerminalState = LlmReplyTerminalState.Completed,
            ReplyText = "hello",
        };

        var next = InvokeAgentTransition(runtime, new AgentRunGAgentState(), fresh);

        next.Status.Should().Be(AgentRunStatus.ReplyProduced);
        next.ProducedReplyText.Should().Be("hello");
        next.ProducedTerminalState.Should().Be(LlmReplyTerminalState.Completed);
    }

    [Fact]
    public async Task ProduceAndDispatch_WhenPersistDispatchedFails_DoesNotDeliverDuplicateFallbackReply()
    {
        // Once DispatchReadyEventAsync delivers the reply to the conversation actor, the user
        // has the response. If PersistReplyDispatchedAsync then fails, the actor MUST swallow
        // that error locally — otherwise HandleStartAsync's outer `catch (Exception)` would
        // call FailAfterUnexpectedExceptionAsync, which would re-enter ProduceAndDispatchAsync
        // with the "Sorry, I couldn't complete this reply" fallback and deliver a SECOND
        // user-visible message on top of the real one.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "the real reply" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });
        // Inject a transient failure on the AgentRunReplyDispatchedEvent persist only.
        runtime.EventSourcing = new FailOnEventTypeSourcing<AgentRunGAgentState, AgentRunReplyDispatchedEvent>(
            (current, evt) => InvokeAgentTransition(runtime, current, evt));

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-dispatched-persist-fail",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-dispatched-persist-fail",
        });

        // Exactly one reply delivered to the conversation actor — the real one. No duplicate
        // fallback was emitted.
        handled.Should().HaveCount(1);
        var ready = handled[0].Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("the real reply");
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Completed);
        replyGenerator.CallCount.Should().Be(1);

        // State stays at REPLY_PRODUCED (the Dispatched event failed to persist, so
        // status is NOT promoted to REPLY_HANDED_OFF). The actor lingers until idle
        // eviction — acceptable trade-off vs. delivering a duplicate user-visible fallback.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        runtime.State.ProducedReplyText.Should().Be("the real reply");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldIgnoreDuplicateStart_AfterReadyAcceptedAndTerminalPersisted()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-duplicate",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-duplicate",
        };

        await runtime.HandleStartAsync(request);
        await runtime.HandleStartAsync(request.Clone());

        // First call ran the LLM and dispatched the ready event, promoting status to
        // REPLY_HANDED_OFF (ADR-0021). The duplicate start must short-circuit on
        // terminal-status check and NOT re-run the LLM or re-dispatch.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_ShouldScheduleTerminalCleanupAfterReplyProduced()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            callbackScheduler: scheduler);

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup-schedule",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup-schedule",
        });

        var cleanup = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunCleanupRequested.Descriptor)).Subject;
        cleanup.ActorId.Should().Be(runtime.Id);
        cleanup.DueTime.Should().Be(AgentRunGAgent.TerminalCleanupDelay);
        var cleanupCommand = cleanup.TriggerEnvelope.Payload.Unpack<AgentRunCleanupRequested>();
        cleanupCommand.RunId.Should().Be("corr-cleanup-schedule");
    }

    [Fact]
    public async Task HandleCleanupAsync_ShouldDestroyTerminalRunActor()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-cleanup",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-cleanup",
        });
        await runtime.HandleCleanupAsync(new AgentRunCleanupRequested
        {
            RunId = "corr-cleanup",
        });

        actorRuntime.DestroyedIds.Should().Contain(runtime.Id);
    }

    [Fact]
    public async Task HandleStartAsync_OnOutputDispatchFailure_PersistsProducedReply_AndRetryReDispatchesWithoutRerunningLlm()
    {
        // Iron rule: output-dispatch failure must NOT replay the LLM/tool chain. The first
        // turn produces the reply, persists it to state, and only then attempts dispatch.
        // The retry must read from state and only re-deliver — repeating the LLM call could
        // repeat tool side effects (SSH exec, external API calls) and incur duplicate billing.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "ok" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-retry-ready",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-retry-ready",
        };

        await runtime.HandleStartAsync(request);

        // After the first call the LLM ran once and the produced payload is persisted, but
        // dispatch failed so status stayed at REPLY_PRODUCED (no promotion to REPLY_HANDED_OFF).
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        runtime.State.ProducedReplyText.Should().Be("ok");
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().BeEmpty();

        var retry = scheduler.Timeouts.Should().ContainSingle(
            timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunStartRequested.Descriptor)).Subject;
        retry.ActorId.Should().Be(runtime.Id);
        retry.DueTime.Should().Be(AgentRunGAgent.OutputDispatchRetryDelay);
        var retryCommand = retry.TriggerEnvelope.Payload.Unpack<AgentRunStartRequested>();

        await runtime.HandleStartAsync(retryCommand);

        // After the retry the same persisted reply is delivered — but the LLM was not
        // re-invoked. Status promoted to REPLY_HANDED_OFF by ApplyReplyDispatched (ADR-0021).
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        replyGenerator.CallCount.Should().Be(1);
        handled.Should().ContainSingle(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_ShouldScheduleRetry_WhenDropSignalIsNotAccepted()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime)
        {
            FailNextSend = true,
        };
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            },
            eventPublisher: publisher,
            callbackScheduler: scheduler);
        var request = new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-retry-drop",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        };

        await runtime.HandleStartAsync(request);

        runtime.State.Status.Should().Be(AgentRunStatus.Started);
        handled.Should().BeEmpty();
        replyGenerator.CallCount.Should().Be(0);

        var retryCommand = scheduler.Timeouts.Should().ContainSingle(
                timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunStartRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunStartRequested>();

        await runtime.HandleStartAsync(retryCommand);

        runtime.State.Status.Should().Be(AgentRunStatus.Dropped);
        replyGenerator.CallCount.Should().Be(0);
        handled.Should().ContainSingle(e => e.Payload.Is(DeferredLlmReplyDroppedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleStartAsync_OnUnexpectedException_PersistsFailedProducedReply_AndDispatchesFallback()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new FailingOnceGetActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                StreamingRepliesEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-unexpected",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-unexpected",
        });

        // The unhandled exception fires the persist-before-dispatch path: the failure
        // terminal state lands as ProducedTerminalState=Failed with a user-visible fallback,
        // and dispatch succeeds so status is promoted to REPLY_HANDED_OFF (ADR-0021).
        // The LLM was never invoked.
        runtime.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runtime.State.ProducedTerminalState.Should().Be(LlmReplyTerminalState.Failed);
        runtime.State.ErrorCode.Should().Be("agent_run_unhandled_exception");
        replyGenerator.CallCount.Should().Be(0);
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("agent_run_unhandled_exception");
    }

    [Fact]
    public async Task HandleStartAsync_RelayTurnCapturesInteractiveIntentIntoReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() =>
        {
            var intent = new MessageContent
            {
                Text = "Choose one",
            };
            intent.Actions.Add(new ActionElement
            {
                Kind = ActionElementKind.Button,
                ActionId = "confirm",
                Label = "Confirm",
                IsPrimary = true,
            });
            return collector.Capture(intent);
        });
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-1",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-1",
        });

        replyGenerator.CaptureSucceeded.Should().BeTrue();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("Choose one");
        ready.Outbound.Actions.Should().ContainSingle();
        ready.Outbound.Actions[0].ActionId.Should().Be("confirm");
    }

    [Fact]
    public async Task HandleStartAsync_NonRelayTurnDoesNotEnableInteractiveScope()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => collector.Capture(new MessageContent { Text = "ignored" }))
        {
            ReplyText = "plain reply",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-2",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-2",
                Content = new MessageContent { Text = "hello" },
            },
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.Outbound.Text.Should().Be("plain reply");
        ready.Outbound.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitFailedReply_WhenGeneratorThrows()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new ThrowingReplyGenerator(new InvalidOperationException("boom"));
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-throw",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-throw",
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_failed");
        ready.ErrorSummary.Should().Be("boom");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitTimeoutFallbackReply_WhenGeneratorHangsPastBudget()
    {
        // Without a cancellation budget on the LLM run, a tool that hangs (broken sandbox,
        // unreachable proxy upstream, slow remote SSH) would pin the run actor turn indefinitely
        // and Lark would stay on the loading reaction forever. The runtime caps each turn at
        // the relay ResponseTimeoutSeconds and folds the cancellation into a user-visible
        // fallback reply with errorCode=llm_reply_timeout.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new HangingReplyGenerator();
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = true,
                ResponseTimeoutSeconds = 1,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-timeout",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-timeout",
        });

        replyGenerator.WasCancelled.Should().BeTrue();
        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("llm_reply_timeout");
        ready.ErrorSummary.Should().Contain("1s budget");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEmitFailedReply_WhenGeneratorReturnsEmpty()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "   ",
        };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-empty",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-empty",
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.TerminalState.Should().Be(LlmReplyTerminalState.Failed);
        ready.ErrorCode.Should().Be("empty_reply");
        ready.Outbound.Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldEchoReplyTokenIntoLlmReplyReadyEvent()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false) { ReplyText = "ok" },
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds();
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-echo",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-echo",
            ReplyTokenExpiresAtUnixMs = expiresAtUnixMs,
        });

        handled.Should().NotBeNull();
        var ready = handled!.Payload.Unpack<LlmReplyReadyEvent>();
        ready.ReplyToken.Should().Be("relay-token-echo");
        ready.ReplyTokenExpiresAtUnixMs.Should().Be(expiresAtUnixMs);
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropRelayRequest_WhenRunCommandCarriesNoReplyToken()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        // Relay activity but no command-carried ReplyToken — simulates a request rehydrated
        // from persisted state after a pod restart, where the original token capture is gone.
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-token",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-token");
        dropped.Reason.Should().Be("missing_relay_reply_token");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropRequest_WhenOlderThanMaxAge()
    {
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "should not run" };
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var requestedAtUnixMs = DateTimeOffset.UtcNow
            .AddMilliseconds(-(AgentRunGAgent.MaxRunRequestAgeMs + 60_000))
            .ToUnixTimeMilliseconds();
        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stale",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stale",
            RequestedAtUnixMs = requestedAtUnixMs,
        });

        replyGenerator.CaptureSucceeded.Should().BeFalse();
        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-stale");
        dropped.Reason.Should().Be("stale_agent_run_request_dropped");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldDropSilently_WhenTargetActorIdMissing()
    {
        var actorRuntime = Substitute.For<IActorRuntime, IActorDispatchPort>();
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-missing",
            TargetActorId = string.Empty,
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
        });

        await actorRuntime.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleStartAsync_ShouldNotifyActor_WhenActivityMissing()
    {
        // Malformed payload (no Activity) should still tell the actor to retire its
        // pending entry — the actor decides whether to clean up. Otherwise the entry
        // accumulates silently in State.PendingLlmReplyRequests until rehydration.
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        EventEnvelope? handled = null;
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled = call.Arg<EventEnvelope>());
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            new RecordingReplyGenerator(() => false),
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-no-activity",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
        });

        handled.Should().NotBeNull();
        var dropped = handled!.Payload.Unpack<DeferredLlmReplyDroppedEvent>();
        dropped.CorrelationId.Should().Be("corr-no-activity");
        dropped.Reason.Should().Be("malformed_deferred_llm_reply_request");
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabled_DispatchesChunkEventAndReadyEvent()
    {
        // Pin the legacy edit-message path explicitly: card-mode is now the default
        // (StreamingCardKitEnabled=true) and emits a structurally distinct
        // LlmReplyCardStreamChunkEvent. This test specifically exercises the
        // text-edit chunk shape, so opt out of card mode here.
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "streamed reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingFlushIntervalMs = 0,
                StreamingCardKitEnabled = false,
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-stream",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        handled.Any(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor)).Should().BeTrue();
        handled.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = handled.First(e => e.Payload.Is(LlmReplyStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("streamed reply");
        chunk.CorrelationId.Should().Be("corr-stream");
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabledWithDefaultCardMode_DispatchesCardChunkEvent()
    {
        // Pinning the new default: StreamingCardKitEnabled=true causes the sink to emit
        // the card-mode chunk type, exercising the CardKit lifecycle entrypoint without
        // needing a real ChannelCardConversationTurnRunner wired up (the actor is mocked,
        // so we only verify the run actor dispatched the right proto type to the actor).
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "card streamed reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_2");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                InteractiveRepliesEnabled = false,
                StreamingRepliesEnabled = true,
                StreamingCardKitFlushIntervalMs = 0,
                // StreamingCardKitEnabled defaults to true.
            });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-card-stream",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-card-stream",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        handled.Any(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor)).Should().BeTrue();
        handled.Any(e => e.Payload.Is(LlmReplyReadyEvent.Descriptor)).Should().BeTrue();
        var chunk = handled.First(e => e.Payload.Is(LlmReplyCardStreamChunkEvent.Descriptor))
            .Payload.Unpack<LlmReplyCardStreamChunkEvent>();
        chunk.AccumulatedText.Should().Be("card streamed reply");
        chunk.CorrelationId.Should().Be("corr-card-stream");
    }

    [Fact]
    public async Task HandleStartAsync_StreamingDisabledFlag_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:group:oc_group_chat_1");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = false });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-legacy",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = BuildRelayActivity(),
            ReplyToken = "relay-token-legacy",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });

        handled.Should().ContainSingle();
        handled[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_StreamingEnabledButNonRelay_DispatchesOnlyReadyEvent()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        var replyGenerator = new RecordingReplyGenerator(() => false) { ReplyText = "plain reply" };
        var actor = Substitute.For<IActor>();
        actor.Id.Returns("channel-conversation:lark:dm:user");
        var handled = new List<EventEnvelope>();
        actor.When(x => x.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => handled.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));
        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            collector,
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = false, StreamingRepliesEnabled = true });

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-nonrelay",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "msg-nonrelay",
                Content = new MessageContent { Text = "hello" },
                // No OutboundDelivery → not a relay turn
            },
        });

        handled.Should().ContainSingle();
        handled[0].Payload.Is(LlmReplyReadyEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleStartAsync_ShouldApplyBotOwnerLlmConfig_FromUserConfigQueryPort()
    {
        // Bot owner's LLM model + route comes from UserConfig (the same store that backs
        // their nyxid-chat preferences), looked up by the scope id resolved from the
        // bot registration. The relay turn uses the inbound user-token as the bearer
        // (it is the bot owner's own NyxID session, freshly issued per callback) while
        // taking model / route / max-tool-rounds from the owner's pre-configured
        // UserConfig.
        var capturedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            MetadataObserver = m =>
            {
                foreach (var pair in m)
                    capturedMetadata[pair.Key] = pair.Value;
            },
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));

        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-bot", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("scope-bot-owner"));

        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-bot-owner", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Aevatar.Studio.Application.Studio.Abstractions.UserConfig(
                DefaultModel: "gpt-4o-bot-owner",
                PreferredLlmRoute: "/api/v1/proxy/s/anthropic-via-bot-owner",
                RuntimeMode: "local",
                LocalRuntimeBaseUrl: "http://localhost",
                RemoteRuntimeBaseUrl: "https://example.com",
                GithubUsername: null,
                MaxToolRounds: 11)));

        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true },
            scopeResolver,
            userConfigQueryPort);

        var activity = BuildRelayActivity();
        activity.Bot = BotInstanceId.From("api-key-bot");
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bot-owner",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-bot-owner",
        });

        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.ModelOverride)
            .WhoseValue.Should().Be("gpt-4o-bot-owner");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference)
            .WhoseValue.Should().Be("/api/v1/proxy/s/anthropic-via-bot-owner");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.MaxToolRoundsOverride)
            .WhoseValue.Should().Be("11");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdAccessToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdOrgToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldThreadBotOwnerSessionTokenAsLlmBearer()
    {
        // The inbound X-NyxID-User-Token is the bot owner's own NyxID session JWT.
        // It is the credential that would authorize the owner's LLM calls in
        // nyxid-chat, so it is also the correct credential for the bot's relay
        // LLM call. The stale-pending GC plus the direct-enqueue + run-echoed
        // token flow keeps it fresh through the window where the LLM call actually
        // fires.
        var capturedMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var replyGenerator = new RecordingReplyGenerator(() => false)
        {
            ReplyText = "ack",
            MetadataObserver = m =>
            {
                foreach (var pair in m)
                    capturedMetadata[pair.Key] = pair.Value;
            },
        };

        var actor = Substitute.For<IActor>();
        actor.Id.Returns("actor-1");
        var actorRuntime = new DispatchingActorRuntime(("actor-1", actor));

        var runtime = CreateRunAgent(
            actorRuntime,
            replyGenerator,
            new AsyncLocalInteractiveReplyCollector(),
            new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions { InteractiveRepliesEnabled = true });

        var activity = BuildRelayActivity();
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "bot-owner-session-jwt",
        };

        await runtime.HandleStartAsync(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-bearer",
            TargetActorId = "actor-1",
            RegistrationId = "reg-1",
            Activity = activity,
            ReplyToken = "relay-token-1",
        });

        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdAccessToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
        capturedMetadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdOrgToken)
            .WhoseValue.Should().Be("bot-owner-session-jwt");
    }

    private static AgentRunGAgent CreateRunAgent(
        IActorRuntime actorRuntime,
        IConversationReplyGenerator replyGenerator,
        IInteractiveReplyCollector? collector,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions relayOptions,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        IEventPublisher? eventPublisher = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        var dispatchPort = actorRuntime as IActorDispatchPort ?? Substitute.For<IActorDispatchPort>();
        var agent = new AgentRunGAgent(
            actorRuntime,
            dispatchPort,
            replyGenerator,
            collector,
            relayOptions,
            NullLogger<AgentRunGAgent>.Instance,
            scopeResolver,
            userConfigQueryPort,
            callbackScheduler);
        SetId(agent, AgentRunGAgent.BuildActorId(Guid.NewGuid().ToString("N")));
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeAgentTransition(agent, current, evt));
        agent.EventPublisher = eventPublisher ?? new DispatchingEventPublisher(actorRuntime);
        return agent;
    }

    private static void AttachScheduler(AgentRunGAgent agent, RecordingCallbackScheduler scheduler)
    {
        agent.Services = new ServiceCollection()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();
    }

    private static AgentRunGAgentState InvokeAgentTransition(
        AgentRunGAgent agent,
        AgentRunGAgentState current,
        IMessage evt)
    {
        var currentType = agent.GetType();
        while (currentType is not null)
        {
            var transitionMethod = currentType.GetMethod(
                "TransitionState",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (transitionMethod is not null)
                return (AgentRunGAgentState)transitionMethod.Invoke(agent, [current, evt])!;

            currentType = currentType.BaseType;
        }

        throw new InvalidOperationException("Unable to invoke AgentRunGAgent transition via reflection.");
    }

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private static ChatActivity BuildRelayActivity() =>
        new()
        {
            Id = "msg-1",
            ChannelId = ChannelId.From("lark"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc_group_chat_1",
                "group",
                "oc_group_chat_1"),
            Content = new MessageContent { Text = "hello" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-1",
            },
        };

    private sealed class DispatchingActorRuntime(params (string Id, IActor Actor)[] actors) :
        IActorRuntime,
        IActorDispatchPort
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(
            static pair => pair.Id,
            static pair => pair.Actor,
            StringComparer.Ordinal);

        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public List<string> DestroyedIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            if (_actors.TryGetValue(actorId, out var existing))
                return Task.FromResult(existing);

            var actor = Substitute.For<IActor>();
            actor.Id.Returns(actorId);
            _actors[actorId] = actor;
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<ConversationGAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedIds.Add(id);
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            if (!_actors.TryGetValue(actorId, out var actor))
                throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
        }
    }

    private sealed class FailingOnceGetActorRuntime(params (string Id, IActor Actor)[] actors) : IActorRuntime
    {
        private readonly DispatchingActorRuntime _inner = new(actors);
        private bool _failNextGet = true;

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            _inner.CreateAsync<TAgent>(id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            _inner.CreateAsync(agentType, id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            _inner.DestroyAsync(id, ct);

        public Task<IActor?> GetAsync(string id)
        {
            if (_failNextGet)
            {
                _failNextGet = false;
                throw new InvalidOperationException("actor runtime lookup failed");
            }

            return _inner.GetAsync(id);
        }

        public Task<bool> ExistsAsync(string id) => _inner.ExistsAsync(id);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            _inner.LinkAsync(parentId, childId, ct);

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            _inner.UnlinkAsync(childId, ct);
    }

    /// <summary>
    /// Test stub that fails <see cref="ConfirmEventsAsync"/> only when an event of type
    /// <typeparamref name="TFailEvent"/> is in the pending list. Used to simulate
    /// "persistence succeeded for produced event but failed for dispatched event" so we
    /// can verify the actor does NOT escalate that into a duplicate fallback reply.
    /// </summary>
    private sealed class FailOnEventTypeSourcing<TState, TFailEvent>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
        where TFailEvent : IMessage
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            if (_pending.OfType<TFailEvent>().Any())
            {
                _pending.Clear();
                throw new InvalidOperationException(
                    $"Simulated persistence failure for event type {typeof(TFailEvent).Name}");
            }
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult { LatestVersion = CurrentVersion });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents() => _pending.Clear();

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
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

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public List<RuntimeCallbackTimerRequest> Timers { get; } = [];

        public List<RuntimeCallbackLease> Cancelled { get; } = [];

        public List<string> PurgedActorIds { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            Timers.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timers.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            Cancelled.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            PurgedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class DispatchingEventPublisher(IActorRuntime actorRuntime) : IEventPublisher
    {
        public bool FailNextSend { get; set; }

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Task PublishAsync<T>(
            T e,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage => Task.CompletedTask;

        public async Task SendToAsync<T>(
            string targetActorId,
            T e,
            CancellationToken c = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new InvalidOperationException("send not accepted");
            }

            Sent.Add((targetActorId, e));
            var actor = await actorRuntime.GetAsync(targetActorId)
                        ?? throw new InvalidOperationException($"Actor {targetActorId} not found.");
            await actor.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(e),
                Route = EnvelopeRouteSemantics.CreateDirect("agent-run-test-publisher", targetActorId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = sourceEnvelope?.Propagation?.CorrelationId ?? string.Empty,
                },
            }, c);
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public List<(string StreamId, EventEnvelope Envelope)> Produced =>
            _streams.Values.SelectMany(stream => stream.Produced.Select(envelope => (stream.StreamId, envelope))).ToList();

        public IStream GetStream(string actorId)
        {
            if (!_streams.TryGetValue(actorId, out var stream))
            {
                stream = new RecordingStream(actorId);
                _streams[actorId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;

        public List<EventEnvelope> Produced { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            if (message is EventEnvelope envelope)
                Produced.Add(envelope.Clone());
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new() =>
            Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingReplyGenerator(Func<bool> captureAction) : IConversationReplyGenerator
    {
        public string ReplyText { get; init; } = string.Empty;

        public int CallCount { get; private set; }

        public bool CaptureSucceeded { get; private set; }

        public Action<IReadOnlyDictionary<string, string>>? MetadataObserver { get; init; }

        public async Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            CallCount++;
            CaptureSucceeded = captureAction();
            MetadataObserver?.Invoke(metadata);
            if (streamingSink is not null && !string.IsNullOrEmpty(ReplyText))
                await streamingSink.OnDeltaAsync(ReplyText, ct);
            return new ConversationReplyResult(ReplyText, Usage: null, FinishReason: null);
        }
    }

    private sealed class ThrowingReplyGenerator(Exception exception) : IConversationReplyGenerator
    {
        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => Task.FromException<ConversationReplyResult>(exception);
    }

    /// <summary>Generator that never completes on its own; only ends when the runtime cancels it.</summary>
    private sealed class HangingReplyGenerator : IConversationReplyGenerator
    {
        public bool WasCancelled { get; private set; }

        public async Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct)
        {
            var pendingReply = new TaskCompletionSource<ConversationReplyResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = ct.Register(() =>
            {
                WasCancelled = true;
                pendingReply.TrySetCanceled(ct);
            });

            return await pendingReply.Task;
        }
    }
}

internal static class AgentRunGAgentTestExtensions
{
    public static Task HandleStartAsync(this AgentRunGAgent agent, NeedsLlmReplyEvent request) =>
        agent.HandleStartAsync(new AgentRunStartRequested
        {
            Request = request,
        });
}
